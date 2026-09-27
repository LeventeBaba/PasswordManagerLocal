using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeUserControlOperationWriterService : IUserControlOperationWriterService
{
    private readonly IUserRepository _users;
    private readonly IUserDeviceRepository _userDevices;
    private readonly IUserMembershipAuthorizationRepository _authorizations;
    private readonly IUserMembershipAuthorizationService _membershipAuthorization;
    private readonly IUserControlOperationRepository _operations;
    private readonly IUserControlStateRepository _states;
    private readonly IDeviceIdentityService _identity;

    public FakeUserControlOperationWriterService(
        IUserRepository users,
        IUserDeviceRepository userDevices,
        IUserMembershipAuthorizationRepository authorizations,
        IUserMembershipAuthorizationService membershipAuthorization,
        IUserControlOperationRepository operations,
        IUserControlStateRepository states,
        IDeviceIdentityService identity)
    {
        _users = users;
        _userDevices = userDevices;
        _authorizations = authorizations;
        _membershipAuthorization = membershipAuthorization;
        _operations = operations;
        _states = states;
        _identity = identity;
    }

    public int Calls { get; private set; }

    public Task<UserControlOperationEnvelope> CreateAppliedKeyEpochReplacementAsync(User user, long previousKeyEpoch, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Create(user, UserControlOperationType.KeyEpochReplacement, previousKeyEpoch, user.KeyEpoch, user.MembershipEpoch, user.MembershipEpoch));
    }

    public Task<UserControlOperationEnvelope> CreateAppliedDeviceAdditionAsync(User user, EnrollmentEndpoint target, CancellationToken ct = default) =>
        CreateAppliedDeviceAdditionUnderLifecycleAsync(user, target, ct);

    public async Task<UserControlOperationEnvelope> CreateAppliedDeviceAdditionUnderLifecycleAsync(User user, EnrollmentEndpoint target, CancellationToken ct = default)
    {
        Calls++;
        var payload = UserControlOperationEnvelopeUtil.CreateDeviceAdditionPayload(
            user.UId, user.KeyEpoch, user.MembershipEpoch, target.DeviceId, target.OriginInstanceId,
            target.SignPublicKey, target.AgreementPublicKey, target.TlsCertFingerprint, target.DeviceType);
        var envelope = Create(user, UserControlOperationType.DeviceAddition, user.KeyEpoch, user.KeyEpoch, payload.PreviousMembershipEpoch, payload.ResultingMembershipEpoch);
        envelope.OperationHash = RandomNumberGenerator.GetBytes(32);
        await _membershipAuthorization.AuthorizeAdditionAsync(payload, envelope.OperationId, envelope.OperationHash, ct);
        user.MembershipEpoch = payload.ResultingMembershipEpoch;
        user.GenerateIntegrityHash();
        _users.Update(user);
        return envelope;
    }

    public Task<UserControlOperationEnvelope> CreateAppliedDeviceRemovalAsync(User user, DeviceRemovalPayload payload, CancellationToken ct = default) =>
        CreateAppliedDeviceRemovalUnderLifecycleAsync(user, payload, ct);

    public async Task<UserControlOperationEnvelope> CreateAppliedDeviceRemovalUnderLifecycleAsync(User user, DeviceRemovalPayload payload, CancellationToken ct = default)
    {
        Calls++;
        var envelope = Create(user, UserControlOperationType.DeviceRemoval, user.KeyEpoch, user.KeyEpoch, payload.PreviousMembershipEpoch, payload.ResultingMembershipEpoch);
        envelope.OperationHash = RandomNumberGenerator.GetBytes(32);
        foreach (var authorization in await _authorizations.ListActiveForDeviceAsync(user.UId, payload.RemovedDeviceId, ct))
            await _membershipAuthorization.EndAuthorizationAsync(authorization, payload, envelope.OperationId, envelope.OperationHash, ct);

        var link = await _userDevices.GetAsync(user.UId, payload.RemovedDeviceId, ct);
        if (link is not null)
        {
            link.IsDeleted = true;
            link.IsSyncOn = false;
            link.DeletedAt = DateTimeOffset.UtcNow;
            link.LastModifiedAt = DateTimeOffset.UtcNow;
            link.GenerateIntegrityHash();
            _userDevices.Update(link);
        }
        user.MembershipEpoch = payload.ResultingMembershipEpoch;
        user.GenerateIntegrityHash();
        _users.Update(user);
        return envelope;
    }

    public async Task<UserControlOperationEnvelope> CreateAppliedAccountDeletionUnderLifecycleAsync(User user, CancellationToken ct = default)
    {
        Calls++;
        var authorizations = await _authorizations.ListForUserAsync(user.UId, ct);
        var payload = UserControlOperationEnvelopeUtil.CreateAccountDeletionPayload(user, authorizations);
        var state = await _states.GetAsync(user.UId, ct);
        if (state is null)
        {
            state = new UserControlState
            {
                UserId = user.UId,
                LocalOriginInstanceId = _identity.OriginInstanceId,
                NextOriginSequence = 1,
                AppliedKeyEpoch = user.KeyEpoch,
                AppliedMembershipEpoch = user.MembershipEpoch,
                LastUpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _states.AddAsync(state, ct);
        }

        var envelope = new UserControlOperationEnvelope
        {
            OperationId = Guid.NewGuid(),
            UserId = user.UId,
            OperationType = UserControlOperationType.AccountDeletion,
            OriginDeviceId = _identity.LocalDeviceId,
            OriginInstanceId = _identity.OriginInstanceId,
            OriginSequence = state.NextOriginSequence,
            PreviousKeyEpoch = user.KeyEpoch,
            ResultingKeyEpoch = user.KeyEpoch,
            PreviousMembershipEpoch = user.MembershipEpoch,
            ResultingMembershipEpoch = user.MembershipEpoch,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            OperationPayload = UserControlOperationEnvelopeUtil.SerializeAccountDeletionPayload(payload)
        };
        UserControlOperationEnvelopeUtil.FillOriginAuthentication(envelope, _identity);
        state.NextOriginSequence++;
        state.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
        _states.Update(state);
        var receivedAtUtc = DateTimeOffset.UtcNow;
        await _operations.AddAsync(
            UserControlOperationMapping.ToStoredOperation(
                envelope,
                UserControlOperationEnvelopeUtil.Serialize(envelope),
                UserControlOperationStatus.Applied,
                receivedAtUtc,
                receivedAtUtc),
            ct);
        return envelope;
    }

    private static UserControlOperationEnvelope Create(User user, UserControlOperationType type, long previousKey, long resultingKey, long previousMembership, long resultingMembership) => new()
    {
        OperationId = Guid.NewGuid(), UserId = user.UId, OperationType = type,
        PreviousKeyEpoch = previousKey, ResultingKeyEpoch = resultingKey,
        PreviousMembershipEpoch = previousMembership, ResultingMembershipEpoch = resultingMembership
    };
}
