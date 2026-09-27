using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Text.Json;

using PasswordManagerLocal.Common.Backend.Sync.Tombstones;
using PasswordManagerLocal.Common.Backend.Internal.Tombstones;
namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Removes encrypted deletion tombstones only after every installation that was authorized when
/// the deletion was created has authenticated true-merge coverage, or has been authoritatively
/// removed with every accepted origin revision incorporated into canonical knowledge.
/// </summary>
public sealed class UserTombstoneGarbageCollector : IUserTombstoneGarbageCollector
{
    private readonly IUserRepository _users;
    private readonly IDeletedUserBarrierRepository _deletionBarriers;
    private readonly IUserMembershipAuthorizationRepository _authorizations;
    private readonly IUserOriginRemovalCutoffRepository _cutoffs;
    private readonly IUserRevisionKnowledgeRepository _knowledge;
    private readonly IUserSyncSnapshotRepository _snapshots;
    private readonly IUserMembershipAuthorizationService _membershipAuthorization;
    private readonly IServiceProvider _services;
    private readonly IUserDataReaderService _reader;
    private readonly IUserDataWriterService _writer;
    private readonly IUserSnapshotPublisherService _publisher;
    private readonly ISyncQueueWriterService _queueWriter;
    private readonly IPendingSyncActivationService _activation;
    private readonly IUserLifecycleCoordinator _lifecycle;
    private readonly IInteractiveSessionStateService _interactiveSessions;
    private readonly IUnitOfWork _uow;

    public UserTombstoneGarbageCollector(
        IUserRepository users,
        IDeletedUserBarrierRepository deletionBarriers,
        IUserMembershipAuthorizationRepository authorizations,
        IUserOriginRemovalCutoffRepository cutoffs,
        IUserRevisionKnowledgeRepository knowledge,
        IUserSyncSnapshotRepository snapshots,
        IUserMembershipAuthorizationService membershipAuthorization,
        IServiceProvider services,
        IUserDataReaderService reader,
        IUserDataWriterService writer,
        IUserSnapshotPublisherService publisher,
        ISyncQueueWriterService queueWriter,
        IPendingSyncActivationService activation,
        IUserLifecycleCoordinator lifecycle,
        IInteractiveSessionStateService interactiveSessions,
        IUnitOfWork uow)
    {
        _users = users;
        _deletionBarriers = deletionBarriers;
        _authorizations = authorizations;
        _cutoffs = cutoffs;
        _knowledge = knowledge;
        _snapshots = snapshots;
        _membershipAuthorization = membershipAuthorization;
        _services = services;
        _reader = reader;
        _writer = writer;
        _publisher = publisher;
        _queueWriter = queueWriter;
        _activation = activation;
        _lifecycle = lifecycle;
        _interactiveSessions = interactiveSessions;
        _uow = uow;
    }

    public Task<TombstoneGarbageCollectionResult> CollectAsync(Guid userId, CancellationToken ct = default) =>
        _lifecycle.ExecuteAsync(userId, token => CollectWithResolvedKeyUnderLifecycleAsync(userId, token), ct);

    public Task<TombstoneGarbageCollectionResult> CollectAsync(Guid userId, EncryptionKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _lifecycle.ExecuteAsync(userId, token => CollectUnderLifecycleAsync(userId, key, token), ct);
    }

    private async Task<TombstoneGarbageCollectionResult> CollectWithResolvedKeyUnderLifecycleAsync(Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty)
            return TombstoneGarbageCollectionResult.Blocked(userId, TombstoneGarbageCollectionReason.UserNotFound);
        if (await _deletionBarriers.ExistsAsync(userId, ct))
            return TombstoneGarbageCollectionResult.Blocked(userId, TombstoneGarbageCollectionReason.AccountDeleted);

        var user = await _users.GetByIdWithRelationsAsync(userId, ct);
        if (user is null)
            return TombstoneGarbageCollectionResult.Blocked(userId, TombstoneGarbageCollectionReason.UserNotFound);
        var keyResolver = _services.GetRequiredService<IUserSyncKeyResolverService>();
        if (!keyResolver.TryResolve(user, out var key) || key is null)
            return TombstoneGarbageCollectionResult.Blocked(userId, TombstoneGarbageCollectionReason.KeyUnavailable);

        using (key)
            return await CollectUnderLifecycleAsync(userId, key, ct);
    }

    private async Task<TombstoneGarbageCollectionResult> CollectUnderLifecycleAsync(
        Guid userId,
        EncryptionKey key,
        CancellationToken ct)
    {
        await using var transaction = await _uow.BeginTransactionAsync(ct);
        var committed = false;
        try
        {
            if (await _deletionBarriers.ExistsAsync(userId, ct))
            {
                await transaction.RollbackAsync(ct);
                return TombstoneGarbageCollectionResult.Blocked(userId, TombstoneGarbageCollectionReason.AccountDeleted);
            }

            var user = await _users.GetByIdWithRelationsAsync(userId, ct);
            if (user is null)
            {
                await transaction.RollbackAsync(ct);
                return TombstoneGarbageCollectionResult.Blocked(userId, TombstoneGarbageCollectionReason.UserNotFound);
            }

            var capturedKeyEpoch = user.KeyEpoch;
            var capturedMembershipEpoch = user.MembershipEpoch;
            var retainedSnapshots = await _snapshots.ListForUserAsync(userId, ct);
            if (retainedSnapshots.Count > TombstoneConstants.MaxCausalSnapshotEvidenceRowsPerUser)
                return await RollbackBlockedAsync(transaction, userId, TombstoneGarbageCollectionReason.EvidenceLimitExceeded, ct);
            if (retainedSnapshots.Any(row => row.Status is UserSyncSnapshotStatus.Pending or UserSyncSnapshotStatus.RecoveryCandidate))
                return await RollbackBlockedAsync(transaction, userId, TombstoneGarbageCollectionReason.PendingSnapshotMerge, ct);
            if (retainedSnapshots.Any(row => row.Status is UserSyncSnapshotStatus.IsolatedFork or UserSyncSnapshotStatus.IsolatedCorrupt))
                return await RollbackBlockedAsync(transaction, userId, TombstoneGarbageCollectionReason.QuarantinedEvidence, ct);

            var authorizations = await _authorizations.ListForUserAsync(userId, ct);
            var cutoffs = await _cutoffs.ListForUserAsync(userId, ct);
            var knowledge = await _knowledge.ListForUserAsync(userId, ct);
            if (authorizations.Count > TombstoneConstants.MaxMembershipHistoryRowsPerUser ||
                cutoffs.Count > TombstoneConstants.MaxRemovalCutoffRowsPerUser ||
                knowledge.Count > TombstoneConstants.MaxRevisionKnowledgeRowsPerUser)
            {
                return await RollbackBlockedAsync(transaction, userId, TombstoneGarbageCollectionReason.EvidenceLimitExceeded, ct);
            }
            try
            {
                TombstoneGarbageCollectionRules.ValidateEvidenceStructure(
                    userId,
                    capturedKeyEpoch,
                    capturedMembershipEpoch,
                    authorizations,
                    cutoffs,
                    knowledge);
            }
            catch (InvalidDataException)
            {
                return await RollbackBlockedAsync(
                    transaction,
                    userId,
                    TombstoneGarbageCollectionReason.InvalidAuthenticatedEvidence,
                    ct);
            }

            var receiptLoad = await LoadAuthenticatedReceiptsAsync(retainedSnapshots, ct);
            if (receiptLoad.FailureReason is not null)
                return await RollbackBlockedAsync(transaction, userId, receiptLoad.FailureReason.Value, ct);

            using var bundle = await _reader.GetAndVerifyUserDataBundleAsync(user, key, ct);
            var tombstones = TombstoneGarbageCollectionRules.EnumerateTombstones(bundle).ToList();
            if (tombstones.Count == 0)
            {
                await transaction.RollbackAsync(ct);
                return new TombstoneGarbageCollectionResult(userId, false, 0, 0, 0, null, []);
            }
            if (tombstones.Count > TombstoneConstants.MaxRetainedUserDataTombstonesPerUser)
                return await RollbackBlockedAsync(transaction, userId, TombstoneGarbageCollectionReason.EvidenceLimitExceeded, ct, tombstones.Count);

            var knowledgeByOrigin = knowledge.ToDictionary(
                row => (row.OriginDeviceId, row.OriginInstanceId, row.UserKeyEpoch));
            var cutoffByAuthorization = cutoffs
                .GroupBy(row => row.AuthorizationId)
                .ToDictionary(group => group.Key, group => group.OrderBy(row => row.UserKeyEpoch).ToArray());

            var stable = new List<TombstoneDescriptor>();
            var diagnostics = new List<TombstoneGarbageCollectionDiagnostic>();
            foreach (var tombstone in tombstones.OrderBy(item => item.ItemType).ThenBy(item => item.ItemId))
            {
                var evaluation = TombstoneGarbageCollectionRules.Evaluate(
                    userId,
                    tombstone,
                    authorizations,
                    cutoffByAuthorization,
                    knowledgeByOrigin,
                    receiptLoad.Receipts);
                if (evaluation.Reason == TombstoneGarbageCollectionReason.Stable)
                {
                    stable.Add(tombstone);
                }
                else if (diagnostics.Count < TombstoneConstants.MaxTombstoneGarbageCollectionDiagnostics)
                {
                    diagnostics.Add(new TombstoneGarbageCollectionDiagnostic(
                        userId,
                        tombstone.ItemType,
                        tombstone.ItemId,
                        tombstone.Version,
                        evaluation.Reason,
                        evaluation.BlockingDeviceId,
                        evaluation.BlockingOriginInstanceId,
                        evaluation.BlockingKeyEpoch));
                }
            }

            if (diagnostics.Any(item => item.Reason is
                    TombstoneGarbageCollectionReason.MissingCausalReference or
                    TombstoneGarbageCollectionReason.InvalidCausalReference))
            {
                await transaction.RollbackAsync(ct);
                return TombstoneGarbageCollectionResult.Blocked(
                    userId,
                    TombstoneGarbageCollectionReason.InvalidCausalReference,
                    tombstones.Count,
                    diagnostics);
            }

            if (stable.Count == 0)
            {
                await transaction.RollbackAsync(ct);
                return new TombstoneGarbageCollectionResult(
                    userId,
                    false,
                    tombstones.Count,
                    0,
                    tombstones.Count,
                    null,
                    diagnostics);
            }

            var modifiedBlobs = UserDataBlobKind.None;
            foreach (var descriptor in stable)
                modifiedBlobs |= descriptor.Remove();

            var latest = await _users.GetByIdAsNoTrackingAsync(userId, ct);
            if (latest is null || latest.KeyEpoch != capturedKeyEpoch || latest.MembershipEpoch != capturedMembershipEpoch ||
                await _deletionBarriers.ExistsAsync(userId, ct))
            {
                await transaction.RollbackAsync(ct);
                _uow.ClearTrackedChanges();
                return TombstoneGarbageCollectionResult.Blocked(
                    userId,
                    TombstoneGarbageCollectionReason.ConcurrentCanonicalChange,
                    tombstones.Count,
                    diagnostics);
            }

            TombstoneGarbageCollectionRules.SortRemainingTombstones(bundle);
            await _writer.CompactUserDataBundleAsync(bundle, user, key, modifiedBlobs, ct);
            var localSnapshot = await _publisher.GetOrCreateAsync(user, ct);
            await _queueWriter.EnqueueAsync(
                new SyncItem
                {
                    ModelId = userId,
                    ModelType = SyncModelType.User,
                    ChangeType = SyncChangeType.Updated,
                    ChangedAtTs = localSnapshot.CreatedAtUtc.ToUnixTimeMilliseconds()
                },
                localSnapshot.CreatedAtUtc.ToUnixTimeMilliseconds(),
                [],
                touchLocalSyncState: true,
                activateTargets: false,
                ct);

            if (user.KeyEpoch != capturedKeyEpoch || user.MembershipEpoch != capturedMembershipEpoch ||
                await _deletionBarriers.ExistsAsync(userId, ct))
            {
                await transaction.RollbackAsync(ct);
                _uow.ClearTrackedChanges();
                return TombstoneGarbageCollectionResult.Blocked(
                    userId,
                    TombstoneGarbageCollectionReason.ConcurrentCanonicalChange,
                    tombstones.Count,
                    diagnostics);
            }

            await _uow.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            committed = true;

            try
            {
                await _interactiveSessions.InvalidateUserCacheAsync(
                    userId,
                    CancellationToken.None);
            }
            catch
            {
                // Canonical compaction and publication are already durable. Cache entries are
                // best-effort accelerators and must not make a committed collection look failed.
            }

            try
            {
                await _activation.ActivatePendingAsync(CancellationToken.None);
            }
            catch
            {
                // The queue and snapshot are already durable; activation is only a wake-up hint.
            }

            return new TombstoneGarbageCollectionResult(
                userId,
                true,
                tombstones.Count,
                stable.Count,
                tombstones.Count - stable.Count,
                null,
                diagnostics);
        }
        catch
        {
            if (!committed)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                _uow.ClearTrackedChanges();
            }
            throw;
        }
    }

    private async Task<AuthenticatedTombstoneReceiptLoadResult> LoadAuthenticatedReceiptsAsync(
        IReadOnlyList<UserSyncSnapshot> rows,
        CancellationToken ct)
    {
        if (rows.Any(row => row.Status is not (
                UserSyncSnapshotStatus.Pending or
                UserSyncSnapshotStatus.LocalPublished or
                UserSyncSnapshotStatus.IsolatedFork or
                UserSyncSnapshotStatus.MergedReceipt or
                UserSyncSnapshotStatus.RecoveryCandidate or
                UserSyncSnapshotStatus.IsolatedCorrupt or
                UserSyncSnapshotStatus.SupersededBadEvidence)))
        {
            return new AuthenticatedTombstoneReceiptLoadResult(
                new Dictionary<(Guid DeviceId, Guid OriginInstanceId), List<UserSnapshotEnvelope>>(),
                TombstoneGarbageCollectionReason.InvalidAuthenticatedEvidence);
        }

        var receipts = new Dictionary<(Guid DeviceId, Guid OriginInstanceId), List<UserSnapshotEnvelope>>();
        foreach (var row in rows
                     .Where(row => row.Status is UserSyncSnapshotStatus.LocalPublished or UserSyncSnapshotStatus.MergedReceipt)
                     .OrderBy(row => row.OriginDeviceId)
                     .ThenBy(row => row.OriginInstanceId)
                     .ThenBy(row => row.UserKeyEpoch))
        {
            try
            {
                if (row.EnvelopePayload.Length == 0 || row.EnvelopePayload.Length > SyncConstants.MaxUserSnapshotEnvelopeBytes)
                    throw new InvalidDataException("A retained merged receipt has an invalid envelope size.");
                var envelope = JsonSerializer.Deserialize(
                                   row.EnvelopePayload,
                                   BackendJsonSerializerContext.Default.UserSnapshotEnvelope)
                               ?? throw new InvalidDataException("A retained merged receipt is invalid.");
                await _membershipAuthorization.VerifySnapshotAuthorAsync(envelope, ct);
                if (row.UserId != envelope.UserId ||
                    !Hashing.Verify(row.SnapshotHash, envelope.SnapshotHash) ||
                    !Hashing.Verify(row.OriginSignPublicKey, envelope.OriginSignPublicKey) ||
                    !Hashing.Verify(row.OriginSignature, envelope.OriginSignature) ||
                    row.OriginDeviceId != envelope.OriginDeviceId ||
                    row.OriginInstanceId != envelope.OriginInstanceId ||
                    row.OriginRevision != envelope.OriginRevision ||
                    row.UserKeyEpoch != envelope.UserKeyEpoch ||
                    row.MembershipEpoch != envelope.MembershipEpoch ||
                    row.CreatedAtUtc != envelope.CreatedAtUtc)
                {
                    throw new InvalidDataException("A retained merged receipt row conflicts with its immutable envelope.");
                }

                var key = (envelope.OriginDeviceId, envelope.OriginInstanceId);
                if (!receipts.TryGetValue(key, out var list))
                {
                    list = [];
                    receipts.Add(key, list);
                }
                list.Add(envelope);
            }
            catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                return new AuthenticatedTombstoneReceiptLoadResult(
                new Dictionary<(Guid DeviceId, Guid OriginInstanceId), List<UserSnapshotEnvelope>>(),
                TombstoneGarbageCollectionReason.InvalidAuthenticatedEvidence);
            }
        }

        return new AuthenticatedTombstoneReceiptLoadResult(receipts, null);
    }








    private async Task<TombstoneGarbageCollectionResult> RollbackBlockedAsync(
        IUnitOfWorkTransaction transaction,
        Guid userId,
        TombstoneGarbageCollectionReason reason,
        CancellationToken ct,
        int retainedCount = 0)
    {
        await transaction.RollbackAsync(ct);
        return TombstoneGarbageCollectionResult.Blocked(userId, reason, retainedCount);
    }


}
