using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserControlOperationWriterService : IUserControlOperationWriterService
{
    private readonly IUserControlOperationRepository _operations;
    private readonly IUserControlStateRepository _states;
    private readonly IUserRepository _users;
    private readonly IDeviceRepository _devices;
    private readonly IUserDeviceRepository _userDevices;
    private readonly IUserMembershipAuthorizationRepository _authorizationRows;
    private readonly IUserMembershipAuthorizationService _membershipAuthorization;
    private readonly IDeviceIdentityService _identity;
    private readonly IUserLifecycleCoordinator _lifecycle;
    private readonly IUnitOfWork _uow;
    private readonly IUserCanonicalHealthService? _canonicalHealth;
    private readonly IUserLoginIdentityProjectionService? _loginIdentities;

    public UserControlOperationWriterService(
        IUserControlOperationRepository operations,
        IUserControlStateRepository states,
        IUserRepository users,
        IDeviceRepository devices,
        IUserDeviceRepository userDevices,
        IUserMembershipAuthorizationRepository authorizationRows,
        IUserMembershipAuthorizationService membershipAuthorization,
        IDeviceIdentityService identity,
        IUserLifecycleCoordinator lifecycle,
        IUnitOfWork uow,
        IUserCanonicalHealthService? canonicalHealth = null,
        IUserLoginIdentityProjectionService? loginIdentities = null)
    {
        _operations = operations;
        _states = states;
        _users = users;
        _devices = devices;
        _userDevices = userDevices;
        _authorizationRows = authorizationRows;
        _membershipAuthorization = membershipAuthorization;
        _identity = identity;
        _lifecycle = lifecycle;
        _uow = uow;
        _canonicalHealth = canonicalHealth;
        _loginIdentities = loginIdentities;
    }

    public Task<UserControlOperationEnvelope> CreateAppliedKeyEpochReplacementAsync(User resultingCanonicalUser, long previousKeyEpoch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resultingCanonicalUser);
        return _lifecycle.ExecuteAsync(resultingCanonicalUser.UId, token => CreateAppliedKeyEpochReplacementCoreAsync(resultingCanonicalUser, previousKeyEpoch, token), ct);
    }

    public Task<UserControlOperationEnvelope> CreateAppliedDeviceAdditionAsync(User canonicalUser, EnrollmentEndpoint target, CancellationToken ct = default) =>
        _lifecycle.ExecuteAsync(canonicalUser.UId, token => CreateAppliedDeviceAdditionCoreAsync(canonicalUser, target, token), ct);

    public Task<UserControlOperationEnvelope> CreateAppliedDeviceAdditionUnderLifecycleAsync(User canonicalUser, EnrollmentEndpoint target, CancellationToken ct = default) =>
        CreateAppliedDeviceAdditionCoreAsync(canonicalUser, target, ct);

    public Task<UserControlOperationEnvelope> CreateAppliedDeviceRemovalAsync(User canonicalUser, DeviceRemovalPayload payload, CancellationToken ct = default) =>
        _lifecycle.ExecuteAsync(canonicalUser.UId, token => CreateAppliedDeviceRemovalCoreAsync(canonicalUser, payload, token), ct);

    public Task<UserControlOperationEnvelope> CreateAppliedDeviceRemovalUnderLifecycleAsync(User canonicalUser, DeviceRemovalPayload payload, CancellationToken ct = default) =>
        CreateAppliedDeviceRemovalCoreAsync(canonicalUser, payload, ct);

    public Task<UserControlOperationEnvelope> CreateAppliedAccountDeletionUnderLifecycleAsync(User canonicalUser, CancellationToken ct = default) =>
        CreateAppliedAccountDeletionCoreAsync(canonicalUser, ct);

    private async Task<UserControlOperationEnvelope> CreateAppliedKeyEpochReplacementCoreAsync(User user, long previousKeyEpoch, CancellationToken ct)
    {
        if (user.KeyEpoch != checked(previousKeyEpoch + 1))
            throw new InvalidOperationException("A key-epoch operation must advance exactly one epoch.");
        if ((await _operations.ListKeyTransitionsFromAsync(user.UId, previousKeyEpoch, ct)).Count != 0)
            throw new InvalidOperationException("A key-epoch transition from this base epoch already exists.");
        await RequireLocalAuthorActiveAsync(user, previousKeyEpoch, user.MembershipEpoch, ct);

        var state = await GetOrCreateStateAsync(user, previousKeyEpoch, user.MembershipEpoch, ct);
        var payload = UserControlOperationEnvelopeUtil.CreateKeyEpochReplacementPayload(user, previousKeyEpoch);
        var envelope = CreateEnvelope(
            user,
            state,
            UserControlOperationType.KeyEpochReplacement,
            previousKeyEpoch,
            user.KeyEpoch,
            user.MembershipEpoch,
            user.MembershipEpoch,
            UserControlOperationEnvelopeUtil.SerializeKeyEpochReplacementPayload(payload));
        await PersistAppliedEnvelopeAsync(envelope, state, ct);
        state.AppliedKeyEpoch = user.KeyEpoch;
        await _uow.SaveChangesAsync(ct);
        return envelope;
    }

    private async Task<UserControlOperationEnvelope> CreateAppliedDeviceAdditionCoreAsync(User user, EnrollmentEndpoint target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(target);
        if (target.DeviceId == Guid.Empty || target.OriginInstanceId == Guid.Empty)
            throw new InvalidDataException("The target device installation identity is incomplete.");
        if (user.MembershipEpoch <= 0 || user.KeyEpoch <= 0)
            throw new InvalidOperationException("The canonical account epochs are invalid.");
        if ((await _operations.ListMembershipTransitionsFromAsync(user.UId, user.MembershipEpoch, ct)).Count != 0)
            throw new InvalidOperationException("A membership transition from this base epoch already exists.");
        await RequireLocalAuthorActiveAsync(user, user.KeyEpoch, user.MembershipEpoch, ct);

        var activeSameDevice = await _authorizationRows.ListActiveForDeviceAsync(user.UId, target.DeviceId, ct);
        if (activeSameDevice.Count != 0)
            throw new InvalidOperationException("The target device already has an active installation authorization.");

        var state = await GetOrCreateStateAsync(user, user.KeyEpoch, user.MembershipEpoch, ct);
        var payload = UserControlOperationEnvelopeUtil.CreateDeviceAdditionPayload(
            user.UId,
            user.KeyEpoch,
            user.MembershipEpoch,
            target.DeviceId,
            target.OriginInstanceId,
            target.SignPublicKey,
            target.AgreementPublicKey,
            target.TlsCertFingerprint,
            target.DeviceType);
        var envelope = CreateEnvelope(
            user,
            state,
            UserControlOperationType.DeviceAddition,
            user.KeyEpoch,
            user.KeyEpoch,
            payload.PreviousMembershipEpoch,
            payload.ResultingMembershipEpoch,
            UserControlOperationEnvelopeUtil.SerializeDeviceAdditionPayload(payload));

        await PersistAppliedEnvelopeAsync(envelope, state, ct);
        await _membershipAuthorization.AuthorizeAdditionAsync(payload, envelope.OperationId, envelope.OperationHash, ct);
        await ApplyCurrentAdditionAsync(payload, ct);
        user.MembershipEpoch = payload.ResultingMembershipEpoch;
        user.GenerateIntegrityHash();
        _users.Update(user);
        state.AppliedMembershipEpoch = user.MembershipEpoch;
        state.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
        if (_canonicalHealth is not null)
            await _canonicalHealth.UpdateCheckpointAsync(user, ct);
        if (_loginIdentities is not null)
            await _loginIdentities.SetCanonicalAsync(user, user.GetGeneralUserDataVersion(), ct);
        await _uow.SaveChangesAsync(ct);
        return envelope;
    }

    private async Task<UserControlOperationEnvelope> CreateAppliedDeviceRemovalCoreAsync(User user, DeviceRemovalPayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        UserControlOperationEnvelopeUtil.ValidateDeviceRemovalPayload(payload);
        if (payload.UserId != user.UId || payload.KeyEpoch != user.KeyEpoch || payload.PreviousMembershipEpoch != user.MembershipEpoch)
            throw new InvalidOperationException("The removal payload is not based on current canonical state.");
        if ((await _operations.ListMembershipTransitionsFromAsync(user.UId, user.MembershipEpoch, ct)).Count != 0)
            throw new InvalidOperationException("A membership transition from this base epoch already exists.");
        await RequireLocalAuthorActiveAsync(user, user.KeyEpoch, user.MembershipEpoch, ct);

        var active = await _authorizationRows.ListActiveForDeviceAsync(user.UId, payload.RemovedDeviceId, ct);
        if (active.Count == 0)
            throw new InvalidOperationException("The device has no active installation authorization.");
        if (active.Any(row => payload.Origins.All(origin => origin.AuthorizationId != row.AuthorizationId)))
            throw new InvalidDataException("The removal payload does not cover every active installation origin.");

        var state = await GetOrCreateStateAsync(user, user.KeyEpoch, user.MembershipEpoch, ct);
        var envelope = CreateEnvelope(
            user,
            state,
            UserControlOperationType.DeviceRemoval,
            user.KeyEpoch,
            user.KeyEpoch,
            payload.PreviousMembershipEpoch,
            payload.ResultingMembershipEpoch,
            UserControlOperationEnvelopeUtil.SerializeDeviceRemovalPayload(payload));
        await PersistAppliedEnvelopeAsync(envelope, state, ct);

        foreach (var authorization in active)
            await _membershipAuthorization.EndAuthorizationAsync(authorization, payload, envelope.OperationId, envelope.OperationHash, ct);

        var link = await _userDevices.GetAsync(user.UId, payload.RemovedDeviceId, ct);
        if (link is not null)
        {
            link.IsDeleted = true;
            link.DeletedAt = DateTimeOffset.UtcNow;
            link.IsSyncOn = false;
            link.LastModifiedAt = DateTimeOffset.UtcNow;
            _userDevices.Update(link);
        }

        user.MembershipEpoch = payload.ResultingMembershipEpoch;
        user.GenerateIntegrityHash();
        _users.Update(user);
        state.AppliedMembershipEpoch = user.MembershipEpoch;
        state.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
        if (_canonicalHealth is not null)
            await _canonicalHealth.UpdateCheckpointAsync(user, ct);
        if (_loginIdentities is not null)
            await _loginIdentities.SetCanonicalAsync(user, user.GetGeneralUserDataVersion(), ct);
        await _uow.SaveChangesAsync(ct);
        return envelope;
    }

    private async Task<UserControlOperationEnvelope> CreateAppliedAccountDeletionCoreAsync(User user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.UId == Guid.Empty || user.KeyEpoch <= 0 || user.MembershipEpoch <= 0)
            throw new InvalidOperationException("The canonical account identity or epochs are invalid.");
        if ((await _operations.ListForUserAsync(user.UId, ct))
            .Any(operation => operation.OperationType == UserControlOperationType.AccountDeletion &&
                              operation.Status != UserControlOperationStatus.Rejected))
        {
            throw new InvalidOperationException("An authoritative account-deletion operation already exists for this user identity.");
        }

        await RequireLocalAuthorActiveAsync(user, user.KeyEpoch, user.MembershipEpoch, ct);
        var authorizations = await _authorizationRows.ListForUserAsync(user.UId, ct);
        if (authorizations.Count == 0)
            throw new InvalidOperationException("Account deletion requires retained membership authorization history.");

        var state = await GetOrCreateStateAsync(user, user.KeyEpoch, user.MembershipEpoch, ct);
        var payload = UserControlOperationEnvelopeUtil.CreateAccountDeletionPayload(user, authorizations);
        var envelope = CreateEnvelope(
            user,
            state,
            UserControlOperationType.AccountDeletion,
            user.KeyEpoch,
            user.KeyEpoch,
            user.MembershipEpoch,
            user.MembershipEpoch,
            UserControlOperationEnvelopeUtil.SerializeAccountDeletionPayload(payload));
        await PersistAppliedEnvelopeAsync(envelope, state, ct);
        await _uow.SaveChangesAsync(ct);
        return envelope;
    }

    private async Task ApplyCurrentAdditionAsync(DeviceAdditionPayload payload, CancellationToken ct)
    {
        var device = await _devices.GetByIdAsync(payload.NewDeviceId, ct);
        var isNew = device is null;
        if (device is not null &&
            (!device.PublicKey.SequenceEqual(payload.AgreementPublicKey) ||
             !device.SignPublicKey.SequenceEqual(payload.SignPublicKey) ||
             !string.Equals(SyncIdentityUtil.NormalizeFingerprint(device.TlsCertFingerprint), SyncIdentityUtil.NormalizeFingerprint(payload.TlsCertFingerprint), StringComparison.OrdinalIgnoreCase) ||
             device.DeviceType != payload.DeviceType) &&
            await _userDevices.HasAnyActiveLinkForDeviceAsync(payload.NewDeviceId, ct))
        {
            throw new InvalidDataException("The signed addition conflicts with an identity still used by an active membership link.");
        }
        device ??= new Device { Id = payload.NewDeviceId };
        device.PublicKey = payload.AgreementPublicKey.ToArray();
        device.SignPublicKey = payload.SignPublicKey.ToArray();
        device.TlsCertFingerprint = SyncIdentityUtil.NormalizeFingerprint(payload.TlsCertFingerprint);
        device.DeviceType = payload.DeviceType;
        device.IsTrusted = true;
        device.IsBlocked = false;
        device.BlockedReason = null;
        device.BlockedAt = null;
        device.LastSeen = DateTime.UtcNow;
        device.LastModifiedAt = DateTimeOffset.UtcNow;
        device.GenerateIntegrityHash();
        if (isNew)
            await _devices.AddAsync(device, ct);
        else
            _devices.Update(device);

        var link = await _userDevices.GetAsync(payload.UserId, payload.NewDeviceId, ct);
        if (link is null)
        {
            link = new UserDevice
            {
                UserId = payload.UserId,
                DeviceId = payload.NewDeviceId,
                IsSyncOn = true,
                IsDeleted = false,
                LastModifiedAt = DateTimeOffset.UtcNow
            };
            await _userDevices.AddAsync(link, ct);
        }
        else
        {
            link.IsDeleted = false;
            link.DeletedAt = null;
            link.IsSyncOn = true;
            link.LastModifiedAt = DateTimeOffset.UtcNow;
            _userDevices.Update(link);
        }
    }

    private async Task RequireLocalAuthorActiveAsync(User user, long keyEpoch, long membershipEpoch, CancellationToken ct)
    {
        var authorization = await _authorizationRows.GetForSignedEpochAsync(
            user.UId,
            _identity.LocalDeviceId,
            _identity.OriginInstanceId,
            membershipEpoch,
            ct);
        if (authorization is null || !authorization.IsActive ||
            keyEpoch < authorization.MinimumKeyEpoch ||
            (authorization.MaximumKeyEpoch is long maximum && keyEpoch > maximum) ||
            !authorization.SignPublicKey.SequenceEqual(_identity.SignPublicKey))
        {
            throw new UnauthorizedAccessException("This exact local installation is not authorized to create a lifecycle operation.");
        }
    }

    private UserControlOperationEnvelope CreateEnvelope(
        User user,
        UserControlState state,
        UserControlOperationType type,
        long previousKeyEpoch,
        long resultingKeyEpoch,
        long previousMembershipEpoch,
        long resultingMembershipEpoch,
        byte[] payload)
    {
        if (state.HasConflict)
            throw new InvalidOperationException(state.ConflictReason ?? "The account control plane is quarantined.");
        if (state.LocalOriginInstanceId != _identity.OriginInstanceId)
        {
            state.LocalOriginInstanceId = _identity.OriginInstanceId;
            state.NextOriginSequence = 1;
        }
        if (state.NextOriginSequence <= 0)
            throw new InvalidOperationException("The local control-operation sequence is invalid.");

        var envelope = new UserControlOperationEnvelope
        {
            OperationId = Guid.NewGuid(),
            UserId = user.UId,
            OperationType = type,
            OriginDeviceId = _identity.LocalDeviceId,
            OriginInstanceId = _identity.OriginInstanceId,
            OriginSequence = state.NextOriginSequence,
            PreviousKeyEpoch = previousKeyEpoch,
            ResultingKeyEpoch = resultingKeyEpoch,
            PreviousMembershipEpoch = previousMembershipEpoch,
            ResultingMembershipEpoch = resultingMembershipEpoch,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            OperationPayload = payload
        };
        UserControlOperationEnvelopeUtil.FillOriginAuthentication(envelope, _identity);
        return envelope;
    }

    private async Task PersistAppliedEnvelopeAsync(UserControlOperationEnvelope envelope, UserControlState state, CancellationToken ct)
    {
        var serialized = UserControlOperationEnvelopeUtil.Serialize(envelope);
        var receivedAtUtc = DateTimeOffset.UtcNow;
        await _operations.AddAsync(
            UserControlOperationMapping.ToStoredOperation(
                envelope,
                serialized,
                UserControlOperationStatus.Applied,
                receivedAtUtc,
                receivedAtUtc),
            ct);
        state.NextOriginSequence = checked(state.NextOriginSequence + 1);
        state.AppliedKeyEpoch = envelope.ResultingKeyEpoch;
        state.AppliedMembershipEpoch = envelope.ResultingMembershipEpoch;
        state.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private async Task<UserControlState> GetOrCreateStateAsync(User user, long appliedKeyEpoch, long appliedMembershipEpoch, CancellationToken ct)
    {
        var state = await _states.GetAsync(user.UId, ct);
        if (state is not null)
            return state;
        state = new UserControlState
        {
            UserId = user.UId,
            LocalOriginInstanceId = _identity.OriginInstanceId,
            NextOriginSequence = 1,
            AppliedKeyEpoch = appliedKeyEpoch,
            AppliedMembershipEpoch = appliedMembershipEpoch,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await _states.AddAsync(state, ct);
        return state;
    }

}
