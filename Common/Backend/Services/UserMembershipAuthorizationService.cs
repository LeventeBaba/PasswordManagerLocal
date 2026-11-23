using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserMembershipAuthorizationService : IUserMembershipAuthorizationService
{
    private readonly IUserMembershipAuthorizationRepository _authorizations;
    private readonly IUserOriginRemovalCutoffRepository _cutoffs;
    private readonly IDeviceIdentityService _identity;
    private readonly IUserControlOperationRepository? _operations;

    public UserMembershipAuthorizationService(
        IUserMembershipAuthorizationRepository authorizations,
        IUserOriginRemovalCutoffRepository cutoffs,
        IDeviceIdentityService identity,
        IUserControlOperationRepository? operations = null)
    {
        _authorizations = authorizations;
        _cutoffs = cutoffs;
        _identity = identity;
        _operations = operations;
    }

    public async Task<UserMembershipAuthorization> VerifyLocalInstallationAsync(
        Guid userId, long keyEpoch, long membershipEpoch, CancellationToken ct = default)
    {
        if (userId == Guid.Empty || keyEpoch <= 0 || membershipEpoch <= 0 || !_identity.IsInitialized ||
            _identity.LocalDeviceId == Guid.Empty || _identity.OriginInstanceId == Guid.Empty ||
            _identity.SignPublicKey.Length != 32 || _identity.AgreementPublicKey.Length != 32)
            throw new UnauthorizedAccessException("The current installation authority is unavailable.");

        var active = await _authorizations.ListActiveForDeviceAsync(userId, _identity.LocalDeviceId, ct);
        if (active.Count != 1)
            throw new UnauthorizedAccessException("The current installation authority is ambiguous or absent.");
        var authorization = active[0];
        if (authorization.UserId != userId || authorization.DeviceId != _identity.LocalDeviceId ||
            authorization.OriginInstanceId != _identity.OriginInstanceId ||
            authorization.AuthorizationId == Guid.Empty || !authorization.IsActive ||
            authorization.EndedMembershipEpoch is not null || authorization.RemovalOperationId is not null ||
            authorization.RemovalOperationHash is not null || authorization.EndedAtUtc is not null ||
            authorization.StartedMembershipEpoch <= 0 || authorization.StartedMembershipEpoch > membershipEpoch ||
            authorization.MinimumKeyEpoch <= 0 || keyEpoch < authorization.MinimumKeyEpoch ||
            (authorization.MaximumKeyEpoch is long maximum && keyEpoch > maximum) ||
            !Hashing.Verify(authorization.SignPublicKey, _identity.SignPublicKey) ||
            !Hashing.Verify(authorization.SignPublicKeyHash, Hashing.SHA256Hash(_identity.SignPublicKey)) ||
            !Hashing.Verify(authorization.AgreementPublicKeyHash, Hashing.SHA256Hash(_identity.AgreementPublicKey)) ||
            !string.Equals(SyncIdentityUtil.NormalizeFingerprint(authorization.TlsCertFingerprint),
                SyncIdentityUtil.NormalizeFingerprint(_identity.FingerprintHex), StringComparison.Ordinal) ||
            authorization.DeviceType != _identity.DeviceType ||
            (authorization.IsGenesis
                ? authorization.StartedMembershipEpoch != 1 || authorization.AdditionOperationId is not null ||
                  authorization.AdditionOperationHash is not null
                : authorization.StartedMembershipEpoch <= 1 || authorization.AdditionOperationId != authorization.AuthorizationId ||
                  authorization.AdditionOperationHash is not { Length: SyncConstants.SyncDeltaPayloadHashBytes }))
            throw new UnauthorizedAccessException("The current installation does not hold valid account authority.");

        var historical = await GetAuthorizationAsync(userId, _identity.LocalDeviceId, _identity.OriginInstanceId,
            membershipEpoch, keyEpoch, ct);
        if (historical.AuthorizationId != authorization.AuthorizationId || !historical.IsActive)
            throw new UnauthorizedAccessException("Current and historical installation authority disagree.");
        return authorization;
    }

    public async Task<UserMembershipAuthorization> CreateGenesisAsync(User user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.KeyEpoch <= 0 || user.MembershipEpoch != 1)
            throw new InvalidOperationException("Genesis membership requires a positive key epoch and membership epoch 1.");

        var existing = await _authorizations.GetActiveAsync(user.UId, _identity.LocalDeviceId, _identity.OriginInstanceId, ct);
        if (existing is not null)
            return existing;

        var row = new UserMembershipAuthorization
        {
            AuthorizationId = Guid.NewGuid(),
            UserId = user.UId,
            DeviceId = _identity.LocalDeviceId,
            OriginInstanceId = _identity.OriginInstanceId,
            SignPublicKey = _identity.SignPublicKey.ToArray(),
            SignPublicKeyHash = Hashing.SHA256Hash(_identity.SignPublicKey),
            AgreementPublicKeyHash = Hashing.SHA256Hash(_identity.AgreementPublicKey),
            TlsCertFingerprint = SyncIdentityUtil.NormalizeFingerprint(_identity.FingerprintHex),
            DeviceType = _identity.DeviceType,
            StartedMembershipEpoch = 1,
            MinimumKeyEpoch = user.KeyEpoch,
            IsActive = true,
            IsGenesis = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _authorizations.AddAsync(row, ct);
        return row;
    }

    public async Task<UserMembershipAuthorization> AuthorizeAdditionAsync(
        DeviceAdditionPayload payload,
        Guid operationId,
        byte[] operationHash,
        DateTimeOffset createdAtUtc,
        CancellationToken ct = default)
    {
        UserControlOperationEnvelopeUtil.ValidateDeviceAdditionPayload(payload);
        if (operationId == Guid.Empty || operationHash.Length != SyncConstants.SyncDeltaPayloadHashBytes || createdAtUtc == default)
            throw new InvalidDataException("The authoritative addition reference is invalid.");

        createdAtUtc = UtcDateTimeUtil.ToUtc(createdAtUtc);

        var existing = await _authorizations.GetActiveAsync(payload.UserId, payload.NewDeviceId, payload.NewOriginInstanceId, ct);
        if (existing is not null)
        {
            if (!existing.IsGenesis &&
                existing.AuthorizationId == operationId &&
                existing.StartedMembershipEpoch == payload.ResultingMembershipEpoch &&
                existing.AdditionOperationId == operationId &&
                existing.AdditionOperationHash is not null &&
                Hashing.Verify(existing.AdditionOperationHash, operationHash))
            {
                if (UtcDateTimeUtil.ToUtc(existing.CreatedAtUtc) != createdAtUtc)
                    throw new InvalidDataException("The retained authorization enrollment time does not match the signed addition operation.");
                return existing;
            }
            throw new InvalidOperationException("The exact target installation already has a different active authorization.");
        }

        var retainedSameOrigin = (await _authorizations.ListForUserAsync(payload.UserId, ct))
            .FirstOrDefault(row => row.DeviceId == payload.NewDeviceId && row.OriginInstanceId == payload.NewOriginInstanceId);
        if (retainedSameOrigin is not null)
            throw new InvalidOperationException("A removed installation origin cannot be authorized again. Re-enrollment requires a newly generated OriginInstanceId.");
        if ((await _authorizations.ListActiveForDeviceAsync(payload.UserId, payload.NewDeviceId, ct)).Count != 0)
            throw new InvalidOperationException("The physical device already has a different active installation authorization.");

        var row = new UserMembershipAuthorization
        {
            AuthorizationId = operationId,
            UserId = payload.UserId,
            DeviceId = payload.NewDeviceId,
            OriginInstanceId = payload.NewOriginInstanceId,
            SignPublicKey = payload.SignPublicKey.ToArray(),
            SignPublicKeyHash = Hashing.SHA256Hash(payload.SignPublicKey),
            AgreementPublicKeyHash = Hashing.SHA256Hash(payload.AgreementPublicKey),
            TlsCertFingerprint = SyncIdentityUtil.NormalizeFingerprint(payload.TlsCertFingerprint),
            DeviceType = payload.DeviceType,
            StartedMembershipEpoch = payload.ResultingMembershipEpoch,
            MinimumKeyEpoch = payload.KeyEpoch,
            IsActive = true,
            AdditionOperationId = operationId,
            AdditionOperationHash = operationHash.ToArray(),
            CreatedAtUtc = createdAtUtc
        };
        await _authorizations.AddAsync(row, ct);
        return row;
    }

    public async Task EndAuthorizationAsync(
        UserMembershipAuthorization authorization,
        DeviceRemovalPayload payload,
        Guid operationId,
        byte[] operationHash,
        DateTimeOffset createdAtUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (createdAtUtc == default)
            throw new InvalidDataException("The authoritative removal timestamp is invalid.");
        createdAtUtc = UtcDateTimeUtil.ToUtc(createdAtUtc);
        UserControlOperationEnvelopeUtil.ValidateDeviceRemovalPayload(payload);
        if (payload.UserId != authorization.UserId || payload.RemovedDeviceId != authorization.DeviceId)
            throw new InvalidDataException("The removal operation targets a different membership authorization.");

        if (authorization.AuthorizationId == Guid.Empty ||
            (authorization.IsGenesis
                ? authorization.StartedMembershipEpoch != 1 || authorization.AdditionOperationId is not null ||
                  authorization.AdditionOperationHash is not null
                : authorization.StartedMembershipEpoch <= 1 || authorization.AdditionOperationId != authorization.AuthorizationId ||
                  authorization.AdditionOperationHash is not { Length: SyncConstants.SyncDeltaPayloadHashBytes }))
            throw new InvalidDataException("The membership authorization does not satisfy the current identity invariant.");

        if (operationId == Guid.Empty || operationHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
            throw new InvalidDataException("The authoritative removal reference is invalid.");
        DeviceRemovalEvidenceUtil.ValidateAuthorization(payload, authorization);

        var authorizationId = authorization.AuthorizationId;
        var origins = payload.Origins
            .Where(item => item.AuthorizationId == authorizationId)
            .OrderBy(item => item.UserKeyEpoch)
            .ToList();
        if (origins.Count == 0)
            throw new InvalidDataException("The removal operation does not carry a cutoff for the authorization.");
        if (origins.Any(origin => origin.OriginInstanceId != authorization.OriginInstanceId ||
                                  !Hashing.Verify(authorization.SignPublicKeyHash, origin.SignPublicKeyHash) ||
                                  origin.UserKeyEpoch < authorization.MinimumKeyEpoch ||
                                  (authorization.MaximumKeyEpoch is long maximum && origin.UserKeyEpoch > maximum)))
            throw new InvalidDataException("A removal cutoff does not match the immutable authorization identity or key namespace.");
        if (origins.Any(origin => authorization.IsGenesis
                ? authorization.StartedMembershipEpoch != 1 || origin.AdditionOperationId is not null ||
                  origin.AdditionOperationHash is not null
                : authorization.AdditionOperationId is null || authorization.AdditionOperationHash is null ||
                  origin.AdditionOperationId != authorization.AdditionOperationId || origin.AdditionOperationHash is null ||
                  !Hashing.Verify(origin.AdditionOperationHash, authorization.AdditionOperationHash)))
            throw new InvalidDataException("A removal cutoff does not match the authorization's addition evidence.");

        foreach (var origin in origins)
        {
            var existing = await _cutoffs.GetAsync(payload.UserId, payload.RemovedDeviceId,
                origin.OriginInstanceId, origin.UserKeyEpoch, ct);
            if (existing is not null &&
                (existing.AuthorizationId != authorizationId || existing.RemovalOperationId != operationId ||
                 existing.ResultingMembershipEpoch != payload.ResultingMembershipEpoch ||
                 !Hashing.Verify(existing.RemovalOperationHash, operationHash) ||
                 existing.HighestAcceptedSnapshotRevision != origin.HighestAcceptedSnapshotRevision ||
                 existing.HighestAcceptedControlSequence != origin.HighestAcceptedControlSequence))
                throw new InvalidOperationException("A different removal cutoff already exists for the origin namespace.");
        }

        if (!authorization.IsActive)
        {
            if (authorization.RemovalOperationId != operationId ||
                authorization.RemovalOperationHash is null ||
                !Hashing.Verify(authorization.RemovalOperationHash, operationHash))
                throw new InvalidOperationException("The installation authorization was already ended by another operation.");
        }
        else
        {
            authorization.IsActive = false;
            authorization.EndedMembershipEpoch = payload.ResultingMembershipEpoch;
            authorization.MaximumKeyEpoch = origins.Max(origin => origin.UserKeyEpoch);
            authorization.RemovalOperationId = operationId;
            authorization.RemovalOperationHash = operationHash.ToArray();
            authorization.EndedAtUtc = createdAtUtc;
            authorization.Version = checked(authorization.Version + 1);
            _authorizations.Update(authorization);
        }

        foreach (var origin in origins)
        {
            var existingCutoff = await _cutoffs.GetAsync(
                payload.UserId,
                payload.RemovedDeviceId,
                origin.OriginInstanceId,
                origin.UserKeyEpoch,
                ct);
            if (existingCutoff is not null)
            {
                if (existingCutoff.AuthorizationId == authorizationId &&
                    existingCutoff.RemovalOperationId == operationId &&
                    Hashing.Verify(existingCutoff.RemovalOperationHash, operationHash) &&
                    existingCutoff.HighestAcceptedSnapshotRevision == origin.HighestAcceptedSnapshotRevision &&
                    existingCutoff.HighestAcceptedControlSequence == origin.HighestAcceptedControlSequence)
                    continue;
                throw new InvalidOperationException("A different removal cutoff already exists for the origin namespace.");
            }

            await _cutoffs.AddAsync(new UserOriginRemovalCutoff
            {
                UserId = payload.UserId,
                DeviceId = payload.RemovedDeviceId,
                OriginInstanceId = origin.OriginInstanceId,
                UserKeyEpoch = origin.UserKeyEpoch,
                HighestAcceptedSnapshotRevision = origin.HighestAcceptedSnapshotRevision,
                HighestAcceptedControlSequence = origin.HighestAcceptedControlSequence,
                ResultingMembershipEpoch = payload.ResultingMembershipEpoch,
                AuthorizationId = authorizationId,
                RemovalOperationId = operationId,
                RemovalOperationHash = operationHash.ToArray(),
                CreatedAtUtc = createdAtUtc
            }, ct);
        }
    }

    public async Task<UserMembershipAuthorization> VerifySnapshotAuthorAsync(UserSnapshotEnvelope envelope, CancellationToken ct = default)
    {
        UserSnapshotEnvelopeUtil.ValidateStructureAndHash(envelope);
        var authorization = await GetAuthorizationAsync(
            envelope.UserId,
            envelope.OriginDeviceId,
            envelope.OriginInstanceId,
            envelope.MembershipEpoch,
            envelope.UserKeyEpoch,
            ct);
        UserSnapshotEnvelopeUtil.VerifyWithSigningKey(envelope, authorization.SignPublicKey);

        if (!authorization.IsActive)
        {
            var cutoff = await _cutoffs.GetAsync(envelope.UserId, envelope.OriginDeviceId, envelope.OriginInstanceId, envelope.UserKeyEpoch, ct)
                ?? throw new UnauthorizedAccessException("The removed origin has no authenticated snapshot cutoff.");
            if (envelope.OriginRevision > cutoff.HighestAcceptedSnapshotRevision)
                throw new UnauthorizedAccessException("The snapshot revision is above the authenticated removal cutoff.");
        }
        if (envelope.Coverage.Count != 0)
        {
            var retainedHistory = await _authorizations.ListForUserAsync(envelope.UserId, ct);
            foreach (var covered in envelope.Coverage)
            {
                var history = retainedHistory.SingleOrDefault(row =>
                    row.DeviceId == covered.OriginDeviceId && row.OriginInstanceId == covered.OriginInstanceId);
                if (history?.RemovalOperationId is not Guid removalId) continue;
                if (_operations is null)
                    throw new UnauthorizedAccessException("Signed removal verification is unavailable for the covered origin.");
                var stored = await _operations.GetByIdAsync(removalId, ct);
                if (stored is null || stored.Status != UserControlOperationStatus.Applied)
                    throw new UnauthorizedAccessException("The covered origin has incomplete signed removal evidence.");
                var removalEnvelope = UserControlOperationEnvelopeUtil.Deserialize(stored.EnvelopePayload);
                if (removalEnvelope.UserId != envelope.UserId || removalEnvelope.OperationType != UserControlOperationType.DeviceRemoval ||
                    history.RemovalOperationHash is null || !Hashing.Verify(history.RemovalOperationHash, removalEnvelope.OperationHash))
                    throw new UnauthorizedAccessException("The excluded-origin removal reference is inconsistent.");
                await VerifyControlAuthorAsync(removalEnvelope, ct);
                var removal = UserControlOperationEnvelopeUtil.DeserializeDeviceRemovalPayload(removalEnvelope.OperationPayload);
                DeviceRemovalEvidenceUtil.ValidateAuthorization(removal, history);
                if (removal.ForkExclusion is null) continue;
                DeviceRemovalForkProofUtil.VerifyAuthorization(removal, history);
                if (covered.OriginRevision > 0)
                    throw new UnauthorizedAccessException("The snapshot contains history from an origin excluded by signed removal.");
            }
        }
        return authorization;
    }

    public async Task<UserMembershipAuthorization> VerifyControlAuthorAsync(UserControlOperationEnvelope envelope, CancellationToken ct = default)
    {
        UserControlOperationEnvelopeUtil.ValidateStructureAndHash(envelope);
        var authorization = await GetAuthorizationAsync(
            envelope.UserId,
            envelope.OriginDeviceId,
            envelope.OriginInstanceId,
            envelope.PreviousMembershipEpoch,
            envelope.PreviousKeyEpoch,
            ct);
        UserControlOperationEnvelopeUtil.VerifyWithSigningKey(envelope, authorization.SignPublicKey);

        if (!authorization.IsActive)
        {
            var cutoffs = await _cutoffs.ListForOriginAsync(envelope.UserId, envelope.OriginDeviceId, envelope.OriginInstanceId, ct);
            var maxSequence = cutoffs.Count == 0 ? -1 : cutoffs.Max(item => item.HighestAcceptedControlSequence);
            if (envelope.OriginSequence > maxSequence)
                throw new UnauthorizedAccessException("The control sequence is above the authenticated removal cutoff.");
        }
        return authorization;
    }

    private async Task<UserMembershipAuthorization> GetAuthorizationAsync(
        Guid userId,
        Guid deviceId,
        Guid originInstanceId,
        long membershipEpoch,
        long keyEpoch,
        CancellationToken ct)
    {
        var authorization = await _authorizations.GetForSignedEpochAsync(userId, deviceId, originInstanceId, membershipEpoch, ct)
            ?? throw new UnauthorizedAccessException("The exact device installation was never authorized at the signed membership epoch.");
        if (keyEpoch < authorization.MinimumKeyEpoch ||
            (authorization.MaximumKeyEpoch is long maximum && keyEpoch > maximum))
        {
            throw new UnauthorizedAccessException("The signed key epoch is outside the installation authorization namespace.");
        }
        return authorization;
    }
}
