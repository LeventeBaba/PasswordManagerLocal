using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Text.Json;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserSnapshotPublisherService : IUserSnapshotPublisherService
{
    private readonly IUserSyncSnapshotRepository _snapshots;
    private readonly IUserSyncStateRepository _states;
    private readonly IUserRevisionKnowledgeRepository _knowledge;
    private readonly IDeviceIdentityService _identity;
    private readonly IUnitOfWork _uow;
    private readonly IUserLifecycleCoordinator _lifecycle;
    private readonly IDeletedUserBarrierRepository? _deletionBarriers;
    private readonly IUserCanonicalHealthService? _canonicalHealth;
    private readonly IUserSyncKeyResolverService? _keyResolver;
    private readonly IUserSyncFaultService? _syncFaults;
    private readonly IUserDataRecoveryScheduler? _recoveryScheduler;

    public UserSnapshotPublisherService(
        IUserSyncSnapshotRepository snapshots,
        IUserSyncStateRepository states,
        IUserRevisionKnowledgeRepository knowledge,
        IDeviceIdentityService identity,
        IUnitOfWork uow,
        IUserLifecycleCoordinator lifecycle,
        IDeletedUserBarrierRepository? deletionBarriers = null,
        IUserCanonicalHealthService? canonicalHealth = null,
        IUserSyncKeyResolverService? keyResolver = null,
        IUserSyncFaultService? syncFaults = null,
        IUserDataRecoveryScheduler? recoveryScheduler = null)
    {
        _snapshots = snapshots;
        _states = states;
        _knowledge = knowledge;
        _identity = identity;
        _uow = uow;
        _lifecycle = lifecycle;
        _deletionBarriers = deletionBarriers;
        _canonicalHealth = canonicalHealth;
        _keyResolver = keyResolver;
        _syncFaults = syncFaults;
        _recoveryScheduler = recoveryScheduler;
    }

    public Task<UserSyncSnapshot?> GetLatestAsync(Guid userId, long userKeyEpoch, CancellationToken ct = default) =>
        _snapshots.GetLatestLocalAsync(userId, _identity.LocalDeviceId, _identity.OriginInstanceId, userKeyEpoch, ct);

    public Task<UserSyncSnapshot> GetOrCreateAsync(User user, CancellationToken ct = default)
    {
        if (user.UId == Guid.Empty)
            throw new InvalidOperationException("Cannot publish a snapshot for an invalid user.");

        return _lifecycle.ExecuteAsync(user.UId, token => GetOrCreateCoreAsync(user, null, UserSyncKeyConfidence.UnconfirmedPassword, token), ct);
    }

    public Task<UserSyncSnapshot> GetOrCreateAfterRecoveryAsync(
        User user,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _lifecycle.ExecuteAsync(user.UId, token => GetOrCreateCoreAsync(user, key, keyConfidence, token), ct);
    }

    private async Task<UserSyncSnapshot> GetOrCreateCoreAsync(
        User user,
        EncryptionKey? suppliedKey,
        UserSyncKeyConfidence suppliedKeyConfidence,
        CancellationToken ct)
    {
        if (user.UId == Guid.Empty)
            throw new InvalidOperationException("Cannot publish a snapshot for an invalid user.");
        if (_deletionBarriers is not null && await _deletionBarriers.ExistsAsync(user.UId, ct))
            throw new InvalidOperationException("Cannot publish a snapshot for a permanently deleted account identity.");

        await EnforceCanonicalHealthGateAsync(user, suppliedKey, suppliedKeyConfidence, ct);

        var state = await _states.GetAsync(user.UId, ct);
        var isNewState = state is null;
        if (state is null)
        {
            state = new UserSyncState
            {
                UserId = user.UId,
                LocalOriginInstanceId = _identity.OriginInstanceId,
                NextOriginRevision = 1,
                LastUpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _states.AddAsync(state, ct);
        }
        else if (state.LocalOriginInstanceId != _identity.OriginInstanceId)
        {
            state.LocalOriginInstanceId = _identity.OriginInstanceId;
            state.NextOriginRevision = 1;
            state.LastPublishedContentHash = [];
            state.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            _states.Update(state);
        }

        var remoteCoverage = (await _knowledge.ListForUserAsync(user.UId, ct))
            .Where(item =>
                item.HighestMergedRevision > 0 &&
                item.UserKeyEpoch <= user.KeyEpoch &&
                (item.OriginDeviceId != _identity.LocalDeviceId ||
                 item.OriginInstanceId != _identity.OriginInstanceId ||
                 item.UserKeyEpoch != user.KeyEpoch))
            .Select(item => new UserSnapshotCoverageEntry
            {
                OriginDeviceId = item.OriginDeviceId,
                OriginInstanceId = item.OriginInstanceId,
                UserKeyEpoch = item.UserKeyEpoch,
                OriginRevision = item.HighestMergedRevision
            })
            .ToList();

        // The local origin revision is an identity of the snapshot, not an input that should
        // force another revision every time GetOrCreateAsync is called. Only canonical bytes,
        // membership/key epochs, and newly merged remote knowledge participate in reuse.
        var contentHash = CalculatePublishedContentHash(user, remoteCoverage);
        // The schema deliberately retains one row per local origin namespace and key epoch.
        // A failed health gate may have changed that row to RecoveryCandidate, so lookup must not
        // filter by LocalPublished here or a later verified publication would attempt to insert a
        // duplicate unique row. Reuse without a new revision is allowed only while the retained
        // row is still an eligible local publication.
        var existing = await _snapshots.GetAsync(
            user.UId,
            _identity.LocalDeviceId,
            _identity.OriginInstanceId,
            user.KeyEpoch,
            ct);
        if (existing?.Status == UserSyncSnapshotStatus.LocalPublished &&
            Hashing.Verify(state.LastPublishedContentHash, contentHash))
            return existing;

        var revision = state.NextOriginRevision;
        if (revision <= 0)
            throw new InvalidOperationException("The next local user snapshot revision is invalid.");

        var coverage = remoteCoverage
            .Append(new UserSnapshotCoverageEntry
            {
                OriginDeviceId = _identity.LocalDeviceId,
                OriginInstanceId = _identity.OriginInstanceId,
                UserKeyEpoch = user.KeyEpoch,
                OriginRevision = revision
            })
            .OrderBy(item => item.OriginDeviceId)
            .ThenBy(item => item.OriginInstanceId)
            .ThenBy(item => item.UserKeyEpoch)
            .ToList();

        var createdAtUtc = DateTimeOffset.UtcNow;
        var envelope = new UserSnapshotEnvelope
        {
            UserId = user.UId,
            OriginDeviceId = _identity.LocalDeviceId,
            OriginInstanceId = _identity.OriginInstanceId,
            OriginRevision = revision,
            UserKeyEpoch = user.KeyEpoch,
            MembershipEpoch = user.MembershipEpoch,
            CreatedAtUtc = createdAtUtc,
            User = CreateUserPayload(user, createdAtUtc.ToUnixTimeMilliseconds()),
            Coverage = coverage
        };
        UserSnapshotEnvelopeUtil.FillOriginAuthentication(envelope, _identity);

        var serialized = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            BackendJsonSerializerContext.Default.UserSnapshotEnvelope);

        if (serialized.Length == 0 || serialized.Length > Constants.SyncConstants.MaxUserSnapshotEnvelopeBytes)
            throw new InvalidDataException("The user snapshot envelope size is invalid.");

        var row = existing ?? new UserSyncSnapshot
        {
            UserId = envelope.UserId,
            OriginDeviceId = envelope.OriginDeviceId,
            OriginInstanceId = envelope.OriginInstanceId,
            UserKeyEpoch = envelope.UserKeyEpoch,
            Status = UserSyncSnapshotStatus.LocalPublished
        };

        CopyEnvelopeToRow(envelope, serialized, row);
        row.ReceivedAtUtc = createdAtUtc;
        row.LastReceivedFromDeviceId = null;
        row.Status = UserSyncSnapshotStatus.LocalPublished;
        row.QuarantineReason = null;
        row.ConflictingSnapshotHash = null;

        if (existing is null)
            await _snapshots.AddAsync(row, ct);
        else
            _snapshots.Update(row);

        state.NextOriginRevision = checked(revision + 1);
        state.LastPublishedContentHash = contentHash;
        state.LastUpdatedAtUtc = createdAtUtc;
        if (!isNewState)
            _states.Update(state);

        var localKnowledge = await _knowledge.GetAsync(
            user.UId,
            _identity.LocalDeviceId,
            _identity.OriginInstanceId,
            user.KeyEpoch,
            ct);
        var isNewKnowledge = localKnowledge is null;
        if (localKnowledge is null)
        {
            localKnowledge = new UserRevisionKnowledge
            {
                UserId = user.UId,
                OriginDeviceId = _identity.LocalDeviceId,
                OriginInstanceId = _identity.OriginInstanceId,
                UserKeyEpoch = user.KeyEpoch
            };
            await _knowledge.AddAsync(localKnowledge, ct);
        }

        localKnowledge.HighestStoredRevision = revision;
        localKnowledge.HighestStoredSnapshotHash = envelope.SnapshotHash.ToArray();
        localKnowledge.HighestMergedRevision = revision;
        localKnowledge.LastUpdatedAtUtc = createdAtUtc;
        if (!isNewKnowledge)
            _knowledge.Update(localKnowledge);
        if (_deletionBarriers is not null && await _deletionBarriers.ExistsAsync(user.UId, ct))
            throw new InvalidOperationException("Account deletion won the lifecycle race before snapshot publication committed.");
        await _uow.SaveChangesAsync(ct);
        return row;
    }

    private async Task EnforceCanonicalHealthGateAsync(
        User user,
        EncryptionKey? suppliedKey,
        UserSyncKeyConfidence suppliedKeyConfidence,
        CancellationToken ct)
    {
        if (_canonicalHealth is null)
        {
            // Test-only/legacy construction remains source compatible, but production DI always
            // supplies the full signed-checkpoint health service.
            user.VerifyIntegrity();
            return;
        }

        EncryptionKey? resolvedKey = null;
        var key = suppliedKey;
        var confidence = suppliedKey is null
            ? UserSyncKeyConfidence.UnconfirmedPassword
            : suppliedKeyConfidence;
        try
        {
            if (key is null)
            {
                _keyResolver?.TryResolve(user, out resolvedKey, out confidence);
                key = resolvedKey;
            }
            var health = await _canonicalHealth.VerifyAsync(user, key, confidence, recordFault: true, ct: ct);
            var previouslyBlocked = _syncFaults is not null &&
                                    await _syncFaults.IsPublishingBlockedAsync(user.UId, ct);

            if (health.FullyVerified && _syncFaults is not null)
            {
                await _syncFaults.MarkLocalCanonicalRecoveredAsync(user.UId, ct);
                previouslyBlocked = false;
            }

            if (!health.IsPublishable || (previouslyBlocked && !health.FullyVerified))
            {
                var stale = await GetLatestAsync(user.UId, user.KeyEpoch, ct);
                if (stale is not null)
                {
                    // The last locally published envelope was created only after the normal
                    // canonical health gate passed. Disable it for relay/publication, but retain it
                    // as freshly re-verifiable recovery evidence instead of declaring it corrupt.
                    stale.Status = UserSyncSnapshotStatus.RecoveryCandidate;
                    stale.QuarantineReason = "Local canonical health verification failed; prior local publication retained for recovery only.";
                    stale.ConflictingSnapshotHash = null;
                    _snapshots.Update(stale);
                }

                await _uow.SaveChangesAsync(ct);
                _recoveryScheduler?.Schedule(user.UId, UserDataRecoveryTrigger.PublisherHealthFailure);
                throw new InvalidDataException(
                    "Local canonical user data did not pass the snapshot publication health gate.");
            }
        }
        finally
        {
            resolvedKey?.Dispose();
        }
    }

    private UserSyncPayload CreateUserPayload(User user, long timestamp)
    {
        foreach (var link in user.UserDevices)
            link.VerifyIntegrity();

        var payload = new UserSyncPayload
        {
            UId = user.UId,
            UsernameHash = user.UsernameHash.ToArray(),
            UsernameSalt = user.UsernameSalt.ToArray(),
            GeneralUserDataVersion = user.GetGeneralUserDataVersion(),
            PasswordSalt = user.PasswordSalt.ToArray(),
            EncryptedPayload = user.EncryptedPayload.ToArray(),
            EncryptedGeneralUserDataPayload = user.EncryptedGeneralUserDataPayload.ToArray(),
            EncryptedUserPasswordsDataPayload = user.EncryptedUserPasswordsDataPayload.ToArray(),
            EncryptedUserDevicesDataPayload = user.EncryptedUserDevicesDataPayload.ToArray(),
            UserDataLastModifiedAt = user.UserDataLastModifiedAt,
            GeneralUserDataLastModifiedAt = user.GeneralUserDataLastModifiedAt,
            UserPasswordsDataLastModifiedAt = user.UserPasswordsDataLastModifiedAt,
            UserDevicesDataLastModifiedAt = user.UserDevicesDataLastModifiedAt,
            GroupIds = user.Groups.Select(group => group.Id).Where(id => id != Guid.Empty).Distinct().OrderBy(id => id).ToList(),
            DeviceIds = user.UserDevices
                .Where(link => !link.IsDeleted)
                .Select(link => link.DeviceId)
                .Append(_identity.LocalDeviceId)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .OrderBy(id => id)
                .ToList()
        };
        payload.IntegrityHash = SyncCryptoUtil.CalculateUserHash(payload, timestamp);
        return payload;
    }

    private byte[] CalculatePublishedContentHash(User user, IReadOnlyList<UserSnapshotCoverageEntry> coverage) =>
        Hashing.SHA256Hash(hash =>
        {
            hash.WriteString("PasswordManagerLocal.Common.Backend.UserSnapshot.PublishedContent.v3");
            hash.Write(user.UId);
            hash.Write(user.KeyEpoch);
            hash.Write(user.MembershipEpoch);
            hash.WriteBytes(user.UsernameHash);
            hash.WriteBytes(user.UsernameSalt);
            user.GetGeneralUserDataVersion().WriteTo(hash);
            hash.WriteBytes(user.PasswordSalt);
            hash.WriteBytes(user.EncryptedPayload);
            hash.WriteBytes(user.EncryptedGeneralUserDataPayload);
            hash.WriteBytes(user.EncryptedUserPasswordsDataPayload);
            hash.WriteBytes(user.EncryptedUserDevicesDataPayload);
            hash.Write(user.UserDataLastModifiedAt);
            hash.Write(user.GeneralUserDataLastModifiedAt);
            hash.Write(user.UserPasswordsDataLastModifiedAt);
            hash.Write(user.UserDevicesDataLastModifiedAt);

            foreach (var groupId in user.Groups.Select(group => group.Id).Distinct().OrderBy(id => id))
                hash.Write(groupId);
            foreach (var deviceId in user.UserDevices.Where(link => !link.IsDeleted).Select(link => link.DeviceId).Distinct().OrderBy(id => id))
                hash.Write(deviceId);
            foreach (var item in coverage.OrderBy(item => item.OriginDeviceId).ThenBy(item => item.OriginInstanceId).ThenBy(item => item.UserKeyEpoch))
            {
                hash.Write(item.OriginDeviceId);
                hash.Write(item.OriginInstanceId);
                hash.Write(item.UserKeyEpoch);
                hash.Write(item.OriginRevision);
            }
        });

    private void CopyEnvelopeToRow(UserSnapshotEnvelope envelope, byte[] serialized, UserSyncSnapshot row)
    {
        row.OriginRevision = envelope.OriginRevision;
        row.MembershipEpoch = envelope.MembershipEpoch;
        row.CreatedAtUtc = envelope.CreatedAtUtc;
        row.SnapshotHash = envelope.SnapshotHash.ToArray();
        row.OriginSignPublicKey = envelope.OriginSignPublicKey.ToArray();
        row.OriginSignature = envelope.OriginSignature.ToArray();
        row.EnvelopePayload = serialized;
    }
}
