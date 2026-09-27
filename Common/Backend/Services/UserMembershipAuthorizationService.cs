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

    public UserMembershipAuthorizationService(
        IUserMembershipAuthorizationRepository authorizations,
        IUserOriginRemovalCutoffRepository cutoffs,
        IDeviceIdentityService identity)
    {
        _authorizations = authorizations;
        _cutoffs = cutoffs;
        _identity = identity;
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
        CancellationToken ct = default)
    {
        UserControlOperationEnvelopeUtil.ValidateDeviceAdditionPayload(payload);
        if (operationId == Guid.Empty || operationHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
            throw new InvalidDataException("The authoritative addition reference is invalid.");

        var existing = await _authorizations.GetActiveAsync(payload.UserId, payload.NewDeviceId, payload.NewOriginInstanceId, ct);
        if (existing is not null)
        {
            if (existing.AdditionOperationId == operationId &&
                existing.AdditionOperationHash is not null &&
                Hashing.Verify(existing.AdditionOperationHash, operationHash))
            {
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
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _authorizations.AddAsync(row, ct);
        return row;
    }

    public async Task EndAuthorizationAsync(
        UserMembershipAuthorization authorization,
        DeviceRemovalPayload payload,
        Guid operationId,
        byte[] operationHash,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        UserControlOperationEnvelopeUtil.ValidateDeviceRemovalPayload(payload);
        if (payload.UserId != authorization.UserId || payload.RemovedDeviceId != authorization.DeviceId)
            throw new InvalidDataException("The removal operation targets a different membership authorization.");

        var origins = payload.Origins
            .Where(item => item.AuthorizationId == authorization.AuthorizationId)
            .OrderBy(item => item.UserKeyEpoch)
            .ToList();
        if (origins.Count == 0)
            throw new InvalidDataException("The removal operation does not carry a cutoff for the authorization.");
        if (origins.Any(origin => origin.OriginInstanceId != authorization.OriginInstanceId ||
                                  !Hashing.Verify(authorization.SignPublicKeyHash, origin.SignPublicKeyHash) ||
                                  origin.UserKeyEpoch < authorization.MinimumKeyEpoch ||
                                  (authorization.MaximumKeyEpoch is long maximum && origin.UserKeyEpoch > maximum)))
            throw new InvalidDataException("A removal cutoff does not match the immutable authorization identity or key namespace.");

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
            authorization.EndedAtUtc = DateTimeOffset.UtcNow;
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
                if (existingCutoff.AuthorizationId == authorization.AuthorizationId &&
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
                AuthorizationId = authorization.AuthorizationId,
                RemovalOperationId = operationId,
                RemovalOperationHash = operationHash.ToArray(),
                CreatedAtUtc = DateTimeOffset.UtcNow
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
