using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Text.Json;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserSnapshotMergeCoordinator : IUserSnapshotMergeCoordinator
{
    private readonly IUserRepository _users;
    private readonly IUserMembershipAuthorizationService _membershipAuthorization;
    private readonly IUserSyncSnapshotRepository _snapshots;
    private readonly IUserRevisionKnowledgeRepository _knowledge;
    private readonly IUserDataBundleSyncService _bundleSync;
    private readonly IUserSnapshotPublisherService _publisher;
    private readonly ISyncQueueWriterService _queueWriter;
    private readonly IPendingSyncActivationService _activation;
    private readonly IUnitOfWork _uow;
    private readonly IUserLifecycleCoordinator _lifecycle;
    private readonly IDeletedUserBarrierRepository? _deletionBarriers;
    private readonly IUserTombstoneGarbageCollector? _garbageCollector;
    private readonly IUserLoginIdentityProjectionService? _loginIdentities;
    private readonly IUserSyncFaultService? _syncFaults;

    public UserSnapshotMergeCoordinator(
        IUserRepository users,
        IUserMembershipAuthorizationService membershipAuthorization,
        IUserSyncSnapshotRepository snapshots,
        IUserRevisionKnowledgeRepository knowledge,
        IUserDataBundleSyncService bundleSync,
        IUserSnapshotPublisherService publisher,
        ISyncQueueWriterService queueWriter,
        IPendingSyncActivationService activation,
        IUnitOfWork uow,
        IUserLifecycleCoordinator lifecycle,
        IDeletedUserBarrierRepository? deletionBarriers = null,
        IUserTombstoneGarbageCollector? garbageCollector = null,
        IUserLoginIdentityProjectionService? loginIdentities = null,
        IUserSyncFaultService? syncFaults = null)
    {
        _users = users;
        _membershipAuthorization = membershipAuthorization;
        _snapshots = snapshots;
        _knowledge = knowledge;
        _bundleSync = bundleSync;
        _publisher = publisher;
        _queueWriter = queueWriter;
        _activation = activation;
        _uow = uow;
        _lifecycle = lifecycle;
        _deletionBarriers = deletionBarriers;
        _garbageCollector = garbageCollector;
        _loginIdentities = loginIdentities;
        _syncFaults = syncFaults;
    }

    public Task<bool> TryMergePendingAsync(Guid userId, EncryptionKey key, CancellationToken ct = default) =>
        TryMergePendingAsync(userId, key, UserSyncKeyConfidence.ExplicitlyTrusted, ct);

    public Task<bool> TryMergePendingAsync(
        Guid userId,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        CancellationToken ct = default) =>
        _lifecycle.ExecuteAsync(userId, token => TryMergePendingCoreAsync(userId, key, keyConfidence, token), ct);

    public Task<bool> TryMergePendingUnderLifecycleAsync(Guid userId, EncryptionKey key, CancellationToken ct = default) =>
        TryMergePendingCoreAsync(userId, key, UserSyncKeyConfidence.ExplicitlyTrusted, ct);

    public Task<bool> TryMergePendingUnderLifecycleAsync(
        Guid userId,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        CancellationToken ct = default) =>
        TryMergePendingCoreAsync(userId, key, keyConfidence, ct);

    private async Task<bool> TryMergePendingCoreAsync(
        Guid userId,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        CancellationToken ct)
    {
        if (_deletionBarriers is not null && await _deletionBarriers.ExistsAsync(userId, ct))
            return false;

        await using var transaction = await _uow.BeginTransactionAsync(ct);
        if (_deletionBarriers is not null && await _deletionBarriers.ExistsAsync(userId, ct))
        {
            await transaction.RollbackAsync(ct);
            return false;
        }
        var user = await _users.GetByIdWithRelationsAsync(userId, ct);
        if (user is null)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        var captured = await _snapshots.ListPendingForKeyEpochAsync(userId, user.KeyEpoch, ct);
        if (captured.Count == 0)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        var candidates = new List<(UserSyncSnapshot Row, UserSnapshotEnvelope Envelope)>();
        foreach (var row in captured
                     .OrderBy(snapshot => snapshot.OriginDeviceId)
                     .ThenBy(snapshot => snapshot.OriginInstanceId)
                     .ThenBy(snapshot => snapshot.OriginRevision))
        {
            try
            {
                var envelope = Deserialize(row);
                await _membershipAuthorization.VerifySnapshotAuthorAsync(envelope, ct);
                if (envelope.UserKeyEpoch != user.KeyEpoch || envelope.MembershipEpoch > user.MembershipEpoch)
                {
                    IsolateCorrupt(row, "The snapshot epoch is not safely applicable to canonical state.");
                    await RecordOriginFaultAsync(
                        row,
                        UserSyncFaultKind.InvalidEpoch,
                        "retained-snapshot-epoch-invalid",
                        UserDataBlobKind.None,
                        ct);
                    continue;
                }

                candidates.Add((row, envelope));
            }
            catch (UnauthorizedAccessException ex)
            {
                IsolateCorrupt(row, ex.Message);
                await RecordOriginFaultAsync(
                    row,
                    UserSyncFaultKind.UnauthorizedOrigin,
                    "retained-snapshot-author-unauthorized",
                    UserDataBlobKind.None,
                    ct);
            }
            catch (Exception ex) when (IsCandidateFailure(ex))
            {
                IsolateCorrupt(row, ex.Message);
                await RecordOriginFaultAsync(
                    row,
                    UserSyncFaultKind.IncomingMetadataMismatch,
                    "retained-envelope-or-authorization-invalid",
                    UserDataBlobKind.None,
                    ct);
            }
        }

        if (candidates.Count == 0)
        {
            await CommitIsolationOnlyAsync(transaction, userId, ct);
            return false;
        }

        UserSnapshotMergeBatchResult mergeResult;
        try
        {
            mergeResult = await _bundleSync.TryVerifyAndMergeManyAsync(
                user,
                candidates.Select(candidate => candidate.Envelope).ToArray(),
                key,
                keyConfidence,
                ct);
        }
        catch (DeterministicSyncConflictException ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _uow.ClearTrackedChanges();
            await RecordDeterministicConflictAsync(userId, candidates, ex, ct);
            throw;
        }
        catch (Exception ex) when (IsCandidateFailure(ex))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _uow.ClearTrackedChanges();
            return false;
        }

        var results = mergeResult.Entries.ToDictionary(
            result => (result.OriginDeviceId, result.OriginInstanceId, result.OriginRevision));

        if (!mergeResult.CanonicalVerified)
        {
            var healthyRemoteExists = mergeResult.Entries.Any(entry => entry.Verified);
            var keyIsConfirmed = keyConfidence != UserSyncKeyConfidence.UnconfirmedPassword || healthyRemoteExists;
            if (!keyIsConfirmed)
            {
                // A password-derived key that verifies neither canonical nor any authenticated
                // remote snapshot remains an ordinary authentication failure. Do not persist a
                // corruption conclusion from ambiguous evidence.
                await transaction.RollbackAsync(CancellationToken.None);
                _uow.ClearTrackedChanges();
                return false;
            }

            if (_syncFaults is not null)
            {
                await _syncFaults.RecordAsync(new UserSyncFaultDescriptor
                {
                    UserId = userId,
                    Scope = UserSyncFaultScope.LocalCanonical,
                    Kind = CanonicalFaultKind(mergeResult.CanonicalState),
                    Status = healthyRemoteExists
                        ? UserSyncHealthStatus.AwaitingEvidence
                        : UserSyncHealthStatus.Isolated,
                    AffectedComponent = mergeResult.CanonicalFailedBlobs.ToString(),
                    KeyEpoch = user.KeyEpoch,
                    MembershipEpoch = user.MembershipEpoch,
                    ExpectedHash = user.IntegrityHash,
                    DiagnosticCode = mergeResult.CanonicalDiagnosticCode ?? "canonical-verification-failed",
                    BlocksPublishing = true,
                    BlocksMerge = false,
                    BlocksLogin = true,
                    BlocksGarbageCollection = true,
                    BlocksLifecycle = true
                }, ct);
            }

            foreach (var candidate in candidates)
            {
                var tuple = (candidate.Envelope.OriginDeviceId, candidate.Envelope.OriginInstanceId, candidate.Envelope.OriginRevision);
                if (results.TryGetValue(tuple, out var result) && result.Verified)
                {
                    candidate.Row.Status = UserSyncSnapshotStatus.RecoveryCandidate;
                    candidate.Row.QuarantineReason = null;
                    candidate.Row.ConflictingSnapshotHash = null;
                    _snapshots.Update(candidate.Row);
                    continue;
                }

                IsolateCorrupt(candidate.Row, result?.FailureReason ?? "snapshot-verification-failed");
                await RecordOriginFaultAsync(
                    candidate.Row,
                    CandidateFaultKind(result?.VerificationState ?? UserDataVerificationState.RootDecryptFailure),
                    result?.DiagnosticCode ?? "snapshot-verification-failed",
                    result?.FailedBlobs ?? UserDataBlobKind.All,
                    ct);
            }

            await _uow.SaveChangesAsync(ct);
            if (_loginIdentities is not null)
            {
                await _loginIdentities.RecalculateUnderLifecycleAsync(userId, ct);
                await _uow.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return false;
        }

        var mergedRows = new List<UserSyncSnapshot>();
        foreach (var candidate in candidates)
        {
            var keyTuple = (
                candidate.Envelope.OriginDeviceId,
                candidate.Envelope.OriginInstanceId,
                candidate.Envelope.OriginRevision);
            if (!results.TryGetValue(keyTuple, out var result) || !result.Verified)
            {
                IsolateCorrupt(
                    candidate.Row,
                    result?.FailureReason ?? "The encrypted snapshot could not be decrypted and verified with the active user key.");
                await RecordOriginFaultAsync(
                    candidate.Row,
                    CandidateFaultKind(result?.VerificationState ?? UserDataVerificationState.RootDecryptFailure),
                    result?.DiagnosticCode ?? "snapshot-verification-failed",
                    result?.FailedBlobs ?? UserDataBlobKind.All,
                    ct);
                continue;
            }

            await RecordMergedKnowledgeAsync(candidate.Envelope, ct);
            mergedRows.Add(candidate.Row);
        }

        if (mergedRows.Count == 0)
        {
            await CommitIsolationOnlyAsync(transaction, userId, ct);
            return false;
        }

        await _uow.SaveChangesAsync(ct);

        if (_deletionBarriers is not null && await _deletionBarriers.ExistsAsync(userId, ct))
        {
            await transaction.RollbackAsync(ct);
            _uow.ClearTrackedChanges();
            return false;
        }

        // A verified snapshot can advance authenticated revision knowledge without changing any
        // canonical user-data blob. Publishing that coverage-only change as a new local origin
        // revision causes an acknowledgement loop between peers: A acknowledges B's revision, B
        // acknowledges A's acknowledgement, and both durable queues remain permanently non-empty.
        //
        // Publish and relay a fresh local snapshot only when the merge changed canonical data. The
        // incoming row and merged-revision knowledge are still committed below, so duplicate and
        // anti-entropy handling remain durable. A later real local or merged canonical change will
        // naturally publish the accumulated coverage without creating acknowledgement-of-ack traffic.
        if (mergeResult.CanonicalChanged)
        {
            var localSnapshot = await _publisher.GetOrCreateAsync(user, ct);

            await _queueWriter.EnqueueAsync(
                new SyncItem
                {
                    ModelId = user.UId,
                    ModelType = SyncModelType.User,
                    ChangeType = SyncChangeType.Updated,
                    ChangedAtTs = localSnapshot.CreatedAtUtc.ToUnixTimeMilliseconds()
                },
                localSnapshot.CreatedAtUtc.ToUnixTimeMilliseconds(),
                [],
                touchLocalSyncState: true,
                activateTargets: false,
                ct);
        }

        foreach (var mergedRow in mergedRows)
        {
            mergedRow.Status = UserSyncSnapshotStatus.MergedReceipt;
            mergedRow.QuarantineReason = null;
            mergedRow.ConflictingSnapshotHash = null;
            _snapshots.Update(mergedRow);
            if (_syncFaults is not null)
            {
                await _syncFaults.MarkOriginRecoveredAsync(
                    mergedRow.UserId,
                    mergedRow.OriginDeviceId,
                    mergedRow.OriginInstanceId,
                    mergedRow.UserKeyEpoch,
                    mergedRow.OriginRevision,
                    ct);
            }
        }
        if (_deletionBarriers is not null && await _deletionBarriers.ExistsAsync(userId, ct))
        {
            await transaction.RollbackAsync(ct);
            _uow.ClearTrackedChanges();
            return false;
        }
        await _uow.SaveChangesAsync(ct);
        if (_loginIdentities is not null)
        {
            await _loginIdentities.RecalculateUnderLifecycleAsync(userId, ct);
            await _uow.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);

        if (_garbageCollector is not null)
        {
            try
            {
                await _garbageCollector.CollectAsync(userId, key, CancellationToken.None);
            }
            catch
            {
            }
        }

        try
        {
            await _activation.ActivatePendingAsync(CancellationToken.None);
        }
        catch
        {
        }
        return true;
    }

    private async Task CommitIsolationOnlyAsync(
        IUnitOfWorkTransaction transaction,
        Guid userId,
        CancellationToken ct)
    {
        await _uow.SaveChangesAsync(ct);
        if (_loginIdentities is not null)
        {
            await _loginIdentities.RecalculateUnderLifecycleAsync(userId, ct);
            await _uow.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private async Task RecordOriginFaultAsync(
        UserSyncSnapshot row,
        UserSyncFaultKind kind,
        string diagnosticCode,
        UserDataBlobKind failedBlobs,
        CancellationToken ct)
    {
        if (_syncFaults is null)
            return;
        await _syncFaults.RecordAsync(new UserSyncFaultDescriptor
        {
            UserId = row.UserId,
            Scope = UserSyncFaultScope.SnapshotOrigin,
            Kind = kind,
            Status = UserSyncHealthStatus.Isolated,
            AffectedComponent = failedBlobs == UserDataBlobKind.None ? "snapshot-envelope" : failedBlobs.ToString(),
            OriginDeviceId = row.OriginDeviceId,
            OriginInstanceId = row.OriginInstanceId,
            KeyEpoch = row.UserKeyEpoch,
            MembershipEpoch = row.MembershipEpoch,
            OriginRevision = row.OriginRevision,
            ObservedHash = row.SnapshotHash,
            ConflictingHash = row.ConflictingSnapshotHash ?? [],
            DiagnosticCode = diagnosticCode,
            BlocksMerge = true,
            BlocksGarbageCollection = true
        }, ct);
    }

    private async Task RecordDeterministicConflictAsync(
        Guid userId,
        IReadOnlyList<(UserSyncSnapshot Row, UserSnapshotEnvelope Envelope)> candidates,
        DeterministicSyncConflictException conflict,
        CancellationToken ct)
    {
        if (_syncFaults is null)
            return;

        await using var transaction = await _uow.BeginTransactionAsync(ct);
        foreach (var candidate in candidates)
        {
            await _syncFaults.RecordAsync(new UserSyncFaultDescriptor
            {
                UserId = userId,
                Scope = UserSyncFaultScope.DeterministicItem,
                Kind = UserSyncFaultKind.DeterministicItemConflict,
                Status = UserSyncHealthStatus.TerminalConflict,
                AffectedComponent = $"{conflict.ItemType}:{conflict.ItemId:N}",
                OriginDeviceId = candidate.Row.OriginDeviceId,
                OriginInstanceId = candidate.Row.OriginInstanceId,
                KeyEpoch = candidate.Row.UserKeyEpoch,
                MembershipEpoch = candidate.Row.MembershipEpoch,
                OriginRevision = candidate.Row.OriginRevision,
                ExpectedHash = Convert.FromHexString(conflict.FirstContentHashHex),
                ObservedHash = Convert.FromHexString(conflict.SecondContentHashHex),
                DiagnosticCode = "deterministic-item-version-content-conflict",
                BlocksMerge = true,
                BlocksLogin = true,
                BlocksGarbageCollection = true
            }, ct);
        }
        await _uow.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private UserSyncFaultKind CanonicalFaultKind(UserDataVerificationState state) => state switch
    {
        UserDataVerificationState.RowIntegrityFailure => UserSyncFaultKind.CanonicalIntegrityMismatch,
        UserDataVerificationState.CheckpointMissing => UserSyncFaultKind.CanonicalCheckpointMissing,
        UserDataVerificationState.CheckpointFailure => UserSyncFaultKind.CanonicalCheckpointMismatch,
        UserDataVerificationState.RootDecryptFailure => UserSyncFaultKind.CanonicalRootDecryptFailure,
        UserDataVerificationState.RootIntegrityFailure => UserSyncFaultKind.CanonicalRootIntegrityFailure,
        UserDataVerificationState.GeneralBlobFailure => UserSyncFaultKind.CanonicalGeneralBlobFailure,
        UserDataVerificationState.PasswordsBlobFailure => UserSyncFaultKind.CanonicalPasswordsBlobFailure,
        UserDataVerificationState.DevicesBlobFailure => UserSyncFaultKind.CanonicalDevicesBlobFailure,
        UserDataVerificationState.BundleLinkFailure => UserSyncFaultKind.CanonicalBundleLinkFailure,
        UserDataVerificationState.LoginMetadataFailure => UserSyncFaultKind.LoginProjectionConflict,
        _ => UserSyncFaultKind.CanonicalIntegrityMismatch
    };

    private UserSyncFaultKind CandidateFaultKind(UserDataVerificationState state) => state switch
    {
        UserDataVerificationState.RootDecryptFailure => UserSyncFaultKind.IncomingDecryptFailure,
        UserDataVerificationState.RootIntegrityFailure or
        UserDataVerificationState.GeneralBlobFailure or
        UserDataVerificationState.PasswordsBlobFailure or
        UserDataVerificationState.DevicesBlobFailure or
        UserDataVerificationState.BundleLinkFailure => UserSyncFaultKind.IncomingIntegrityFailure,
        UserDataVerificationState.AuthorizationFailure => UserSyncFaultKind.UnauthorizedOrigin,
        UserDataVerificationState.EpochFailure => UserSyncFaultKind.InvalidEpoch,
        UserDataVerificationState.LoginMetadataFailure => UserSyncFaultKind.IncomingMetadataMismatch,
        _ => UserSyncFaultKind.IncomingIntegrityFailure
    };

    private async Task RecordMergedKnowledgeAsync(UserSnapshotEnvelope envelope, CancellationToken ct)
    {
        await UpsertMergedAsync(
            envelope.UserId,
            envelope.OriginDeviceId,
            envelope.OriginInstanceId,
            envelope.UserKeyEpoch,
            envelope.OriginRevision,
            envelope.SnapshotHash,
            ct);

        foreach (var covered in envelope.Coverage
                     .Where(item => item.UserKeyEpoch <= envelope.UserKeyEpoch && item.OriginRevision > 0)
                     .OrderBy(item => item.OriginDeviceId)
                     .ThenBy(item => item.OriginInstanceId))
        {
            await UpsertMergedAsync(
                envelope.UserId,
                covered.OriginDeviceId,
                covered.OriginInstanceId,
                covered.UserKeyEpoch,
                covered.OriginRevision,
                [],
                ct);
        }
    }

    private async Task UpsertMergedAsync(
        Guid userId,
        Guid originDeviceId,
        Guid originInstanceId,
        long keyEpoch,
        long revision,
        byte[] snapshotHash,
        CancellationToken ct)
    {
        var item = await _knowledge.GetAsync(userId, originDeviceId, originInstanceId, keyEpoch, ct);
        var isNew = item is null;
        item ??= new UserRevisionKnowledge
        {
            UserId = userId,
            OriginDeviceId = originDeviceId,
            OriginInstanceId = originInstanceId,
            UserKeyEpoch = keyEpoch
        };

        // Stored knowledge is advanced only when this exact immutable envelope and hash were
        // durably present. Coverage can advance merged knowledge without claiming that the
        // covered envelope itself is retained locally.
        if (snapshotHash.Length == Constants.SyncConstants.SyncDeltaPayloadHashBytes &&
            revision > item.HighestStoredRevision)
        {
            item.HighestStoredRevision = revision;
            item.HighestStoredSnapshotHash = snapshotHash.ToArray();
        }
        item.HighestMergedRevision = Math.Max(item.HighestMergedRevision, revision);
        item.LastUpdatedAtUtc = DateTimeOffset.UtcNow;

        if (isNew)
            await _knowledge.AddAsync(item, ct);
        else
            _knowledge.Update(item);
    }

    private void IsolateCorrupt(UserSyncSnapshot row, string reason)
    {
        row.Status = UserSyncSnapshotStatus.IsolatedCorrupt;
        row.QuarantineReason = reason.Length <= 512 ? reason : reason[..512];
        row.ConflictingSnapshotHash = null;
        _snapshots.Update(row);
    }

    private bool IsCandidateFailure(Exception ex) =>
        ex is InvalidDataException or
            UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException or
            InvalidDataIntegrityException or
            JsonException;


    private UserSnapshotEnvelope Deserialize(UserSyncSnapshot row)
    {
        if (row.EnvelopePayload.Length == 0 || row.EnvelopePayload.Length > Constants.SyncConstants.MaxUserSnapshotEnvelopeBytes)
            throw new InvalidDataException("The stored user snapshot envelope size is invalid.");

        return JsonSerializer.Deserialize(
                   row.EnvelopePayload,
                   BackendJsonSerializerContext.Default.UserSnapshotEnvelope)
               ?? throw new InvalidDataException("The stored user snapshot envelope is invalid.");
    }
}
