using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Text.Json;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserSnapshotInboxService : IUserSnapshotInboxService
{
    private readonly IUserRepository _users;
    private readonly IUserSyncSnapshotRepository _snapshots;
    private readonly IUserRevisionKnowledgeRepository _knowledge;
    private readonly IDeviceIdentityService _identity;
    private readonly IUnitOfWork _uow;
    private readonly IUserLifecycleCoordinator _lifecycle;
    private readonly IUserMembershipAuthorizationService _membershipAuthorization;
    private readonly IDeletedUserBarrierRepository? _deletionBarriers;
    private readonly IUserLoginIdentityProjectionService? _loginIdentities;
    private readonly ISyncVersionClockService? _versionClock;
    private readonly IUserSyncFaultService? _syncFaults;
    private readonly IUserDataRecoveryScheduler? _recoveryScheduler;

    public UserSnapshotInboxService(
        IUserRepository users,
        IUserSyncSnapshotRepository snapshots,
        IUserRevisionKnowledgeRepository knowledge,
        IDeviceIdentityService identity,
        IUnitOfWork uow,
        IUserLifecycleCoordinator lifecycle,
        IUserMembershipAuthorizationService membershipAuthorization,
        IDeletedUserBarrierRepository? deletionBarriers = null,
        IUserLoginIdentityProjectionService? loginIdentities = null,
        ISyncVersionClockService? versionClock = null,
        IUserSyncFaultService? syncFaults = null,
        IUserDataRecoveryScheduler? recoveryScheduler = null)
    {
        _users = users;
        _snapshots = snapshots;
        _knowledge = knowledge;
        _identity = identity;
        _uow = uow;
        _lifecycle = lifecycle;
        _membershipAuthorization = membershipAuthorization;
        _deletionBarriers = deletionBarriers;
        _loginIdentities = loginIdentities;
        _versionClock = versionClock;
        _syncFaults = syncFaults;
        _recoveryScheduler = recoveryScheduler;
    }

    public async Task<UserSnapshotReceiptResult> StoreAsync(
        UserSnapshotEnvelope envelope,
        Guid transportPeerDeviceId,
        CancellationToken ct = default)
    {
        return await _lifecycle.ExecuteAsync(
            envelope.UserId,
            async token =>
            {
                UserSnapshotReceiptResult receipt;
                await using (var transaction = await _uow.BeginTransactionAsync(token))
                {
                    try
                    {
                        receipt = await StoreCoreAsync(envelope, transportPeerDeviceId, token);
                        await _uow.SaveChangesAsync(token);
                        if (_loginIdentities is not null &&
                            receipt.State is not UserSnapshotReceiptState.Rejected and
                            not UserSnapshotReceiptState.RejectedAccountDeleted and
                            not UserSnapshotReceiptState.WrongKeyEpoch and
                            not UserSnapshotReceiptState.WrongMembershipEpoch and
                            not UserSnapshotReceiptState.ObsoleteRevision)
                        {
                            await _loginIdentities.RecalculateUnderLifecycleAsync(envelope.UserId, token);
                            await _uow.SaveChangesAsync(token);
                        }
                        await transaction.CommitAsync(token);
                    }
                    catch
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        _uow.ClearTrackedChanges();
                        throw;
                    }
                }

                if (_versionClock is not null && IsAcceptedForVersionObservation(receipt.State))
                    _versionClock.Observe([envelope.User.GeneralUserDataVersion]);
                if (receipt.State is UserSnapshotReceiptState.StoredPending or
                    UserSnapshotReceiptState.ReplacedOlderPending or
                    UserSnapshotReceiptState.StoredMergedReceipt or
                    UserSnapshotReceiptState.NeedsKey)
                {
                    _recoveryScheduler?.Schedule(
                        envelope.UserId,
                        UserDataRecoveryTrigger.HealthyCandidateReceived);
                }
                return receipt;
            },
            ct);
    }


    private bool IsAcceptedForVersionObservation(UserSnapshotReceiptState state) =>
        state is UserSnapshotReceiptState.StoredPending or
            UserSnapshotReceiptState.ReplacedOlderPending or
            UserSnapshotReceiptState.AlreadyStored or
            UserSnapshotReceiptState.MergedImmediately or
            UserSnapshotReceiptState.NeedsKey or
            UserSnapshotReceiptState.StoredMergedReceipt;

    private async Task<UserSnapshotReceiptResult> StoreCoreAsync(
        UserSnapshotEnvelope envelope,
        Guid transportPeerDeviceId,
        CancellationToken ct)
    {
        // Expected replays after deletion are acknowledged explicitly before authorization,
        // revision comparison, or pending-row replacement can recreate state.
        if (_deletionBarriers is not null && await _deletionBarriers.ExistsAsync(envelope.UserId, ct))
        {
            return Receipt(envelope, UserSnapshotReceiptState.RejectedAccountDeleted,
                "The account identity is permanently deleted.");
        }

        // Only active identities may cause immutable-envelope validation work. A permanent
        // deletion barrier is authoritative even for malformed or stale replay traffic.
        UserSnapshotEnvelopeUtil.ValidateStructureAndHash(envelope);

        var user = await _users.GetByIdAsync(envelope.UserId, ct);
        if (user is null)
            return Receipt(envelope, UserSnapshotReceiptState.Rejected, "The user does not exist locally.");

        try
        {
            await _membershipAuthorization.VerifySnapshotAuthorAsync(envelope, ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException)
        {
            return Receipt(envelope, UserSnapshotReceiptState.Rejected, ex.Message);
        }

        if (envelope.UserKeyEpoch < user.KeyEpoch)
            return Receipt(envelope, UserSnapshotReceiptState.ObsoleteRevision, "The snapshot uses an obsolete key epoch.");
        if (envelope.UserKeyEpoch > user.KeyEpoch)
            return Receipt(envelope, UserSnapshotReceiptState.WrongKeyEpoch, "The snapshot uses a newer key epoch requiring an explicit replacement operation.");
        if (envelope.MembershipEpoch > user.MembershipEpoch)
            return Receipt(envelope, UserSnapshotReceiptState.WrongMembershipEpoch, "The snapshot uses a newer membership epoch whose predecessor operation is missing.");

        if (envelope.OriginDeviceId == _identity.LocalDeviceId &&
            envelope.OriginInstanceId == _identity.OriginInstanceId)
        {
            await RecordForkAsync(envelope, "duplicate-local-origin", envelope.SnapshotHash, [], ct);
            return Receipt(
                envelope,
                UserSnapshotReceiptState.Quarantined,
                "A transport peer cannot introduce revisions for the current local origin identity.");
        }

        var knowledge = await _knowledge.GetAsync(
            envelope.UserId,
            envelope.OriginDeviceId,
            envelope.OriginInstanceId,
            envelope.UserKeyEpoch,
            ct);
        var existing = await _snapshots.GetAsync(
            envelope.UserId,
            envelope.OriginDeviceId,
            envelope.OriginInstanceId,
            envelope.UserKeyEpoch,
            ct);

        if (existing is not null && existing.Status == UserSyncSnapshotStatus.IsolatedFork)
            return Receipt(envelope, UserSnapshotReceiptState.Quarantined, existing.QuarantineReason ?? "This origin is quarantined.");

        if (knowledge is not null &&
            knowledge.HighestStoredRevision == envelope.OriginRevision &&
            knowledge.HighestStoredSnapshotHash.Length != 0 &&
            !Hashing.Verify(knowledge.HighestStoredSnapshotHash, envelope.SnapshotHash))
        {
            const string reason = "The same origin revision was received with a different snapshot hash.";
            if (existing is null)
            {
                existing = await CreateQuarantinedEvidenceAsync(
                    envelope,
                    transportPeerDeviceId,
                    knowledge.HighestStoredSnapshotHash,
                    reason,
                    ct);
            }
            else
            {
                Quarantine(existing, envelope.SnapshotHash, transportPeerDeviceId, reason);
            }

            await RecordForkAsync(envelope, "same-revision-fork", envelope.SnapshotHash, knowledge.HighestStoredSnapshotHash, ct);
            return Receipt(envelope, UserSnapshotReceiptState.Quarantined, reason);
        }

        // Compare an actually retained envelope before consulting logical merged knowledge.
        // A lower receipt may coexist with newer durable revision knowledge when the newer
        // immutable envelope is no longer retained. Exact replay remains idempotent and an
        // exact-revision hash conflict must still quarantine the whole origin namespace.
        if (existing is not null && existing.OriginRevision == envelope.OriginRevision)
        {
            if (Hashing.Verify(existing.SnapshotHash, envelope.SnapshotHash))
            {
                existing.ReceivedAtUtc = DateTimeOffset.UtcNow;
                existing.LastReceivedFromDeviceId = transportPeerDeviceId;
                if (existing.Status == UserSyncSnapshotStatus.Pending &&
                    knowledge is not null &&
                    knowledge.HighestMergedRevision >= envelope.OriginRevision)
                {
                    existing.Status = UserSyncSnapshotStatus.MergedReceipt;
                    existing.QuarantineReason = null;
                    existing.ConflictingSnapshotHash = null;
                    _snapshots.Update(existing);
                    return Receipt(
                        envelope,
                        UserSnapshotReceiptState.StoredMergedReceipt,
                        "The already-retained envelope was promoted to merged-coverage receipt evidence.");
                }

                _snapshots.Update(existing);
                return Receipt(envelope, UserSnapshotReceiptState.AlreadyStored);
            }

            const string reason = "The same origin revision was received with a different snapshot hash.";
            var retainedHash = existing.SnapshotHash.ToArray();
            Quarantine(existing, envelope.SnapshotHash, transportPeerDeviceId, reason);
            await RecordForkAsync(envelope, "same-revision-fork", envelope.SnapshotHash, retainedHash, ct);
            return Receipt(envelope, UserSnapshotReceiptState.Quarantined, reason);
        }

        if (existing is { Status: UserSyncSnapshotStatus.MergedReceipt } &&
            existing.OriginRevision < envelope.OriginRevision)
        {
            try
            {
                var retainedEnvelope = DeserializeRetainedEnvelope(existing);
                await _membershipAuthorization.VerifySnapshotAuthorAsync(retainedEnvelope, ct);
                if (!CoverageDominates(envelope, retainedEnvelope))
                {
                    const string reason = "A newer snapshot does not dominate the retained merged-coverage receipt.";
                    var retainedHash = existing.SnapshotHash.ToArray();
                    Quarantine(existing, envelope.SnapshotHash, transportPeerDeviceId, reason);
                    await RecordForkAsync(envelope, "revision-rollback", envelope.SnapshotHash, retainedHash, ct);
                    return Receipt(envelope, UserSnapshotReceiptState.Quarantined, reason);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException)
            {
                var retainedHash = existing.SnapshotHash.ToArray();
                Quarantine(existing, envelope.SnapshotHash, transportPeerDeviceId, ex.Message);
                await RecordForkAsync(envelope, "retained-envelope-invalid", envelope.SnapshotHash, retainedHash, ct);
                return Receipt(envelope, UserSnapshotReceiptState.Quarantined, ex.Message);
            }
        }

        // A namespace with an ordinary semantically corrupt retained revision may accept a
        // higher authenticated revision, but that replacement must remain a recovery candidate
        // until a keyed device verifies it. Merged-coverage knowledge from another envelope is
        // not sufficient to make the replacement relayable.
        if (existing?.Status is not (UserSyncSnapshotStatus.IsolatedCorrupt or UserSyncSnapshotStatus.RecoveryCandidate) &&
            knowledge is not null &&
            knowledge.HighestMergedRevision >= envelope.OriginRevision)
        {
            if (existing is not null && existing.OriginRevision > envelope.OriginRevision)
            {
                return Receipt(
                    envelope,
                    UserSnapshotReceiptState.ObsoleteRevision,
                    "A newer authenticated envelope from this origin is already retained.");
            }

            byte[] mergedReceiptPayload;
            try
            {
                mergedReceiptPayload = SerializeEnvelope(envelope);
            }
            catch (InvalidDataException ex)
            {
                return Receipt(envelope, UserSnapshotReceiptState.Rejected, ex.Message);
            }

            var receiptReceivedAtUtc = DateTimeOffset.UtcNow;
            var mergedReceipt = existing ?? new UserSyncSnapshot
            {
                UserId = envelope.UserId,
                OriginDeviceId = envelope.OriginDeviceId,
                OriginInstanceId = envelope.OriginInstanceId,
                UserKeyEpoch = envelope.UserKeyEpoch
            };
            mergedReceipt.OriginRevision = envelope.OriginRevision;
            mergedReceipt.MembershipEpoch = envelope.MembershipEpoch;
            mergedReceipt.CreatedAtUtc = envelope.CreatedAtUtc;
            mergedReceipt.ReceivedAtUtc = receiptReceivedAtUtc;
            mergedReceipt.LastReceivedFromDeviceId = transportPeerDeviceId;
            mergedReceipt.SnapshotHash = envelope.SnapshotHash.ToArray();
            mergedReceipt.OriginSignPublicKey = envelope.OriginSignPublicKey.ToArray();
            mergedReceipt.OriginSignature = envelope.OriginSignature.ToArray();
            mergedReceipt.EnvelopePayload = mergedReceiptPayload;
            mergedReceipt.Status = UserSyncSnapshotStatus.MergedReceipt;
            mergedReceipt.QuarantineReason = null;
            mergedReceipt.ConflictingSnapshotHash = null;

            if (existing is null)
                await _snapshots.AddAsync(mergedReceipt, ct);
            else
                _snapshots.Update(mergedReceipt);

            // Retaining an older available envelope as receipt evidence must not regress the
            // highest exact revision/hash that was ever durably observed. Inventory advertises
            // the retained row and this durable known identity separately.
            if (envelope.OriginRevision > knowledge.HighestStoredRevision ||
                (envelope.OriginRevision == knowledge.HighestStoredRevision &&
                 knowledge.HighestStoredSnapshotHash.Length == 0))
            {
                knowledge.HighestStoredRevision = envelope.OriginRevision;
                knowledge.HighestStoredSnapshotHash = envelope.SnapshotHash.ToArray();
            }
            knowledge.LastUpdatedAtUtc = receiptReceivedAtUtc;
            _knowledge.Update(knowledge);

            return Receipt(
                envelope,
                UserSnapshotReceiptState.StoredMergedReceipt,
                "The authenticated reporting snapshot was retained as merged-coverage evidence.");
        }

        if (existing is not null)
        {
            if (existing.OriginRevision > envelope.OriginRevision)
                return Receipt(envelope, UserSnapshotReceiptState.ObsoleteRevision, "A newer revision from this origin is already stored.");
        }

        byte[] serialized;
        try
        {
            serialized = SerializeEnvelope(envelope);
        }
        catch (InvalidDataException ex)
        {
            return Receipt(envelope, UserSnapshotReceiptState.Rejected, ex.Message);
        }

        var now = DateTimeOffset.UtcNow;
        var isRecoveryCandidate = existing?.Status is UserSyncSnapshotStatus.IsolatedCorrupt or UserSyncSnapshotStatus.RecoveryCandidate;
        var row = existing ?? new UserSyncSnapshot
        {
            UserId = envelope.UserId,
            OriginDeviceId = envelope.OriginDeviceId,
            OriginInstanceId = envelope.OriginInstanceId,
            UserKeyEpoch = envelope.UserKeyEpoch
        };
        row.OriginRevision = envelope.OriginRevision;
        row.MembershipEpoch = envelope.MembershipEpoch;
        row.CreatedAtUtc = envelope.CreatedAtUtc;
        row.ReceivedAtUtc = now;
        row.LastReceivedFromDeviceId = transportPeerDeviceId;
        row.SnapshotHash = envelope.SnapshotHash.ToArray();
        row.OriginSignPublicKey = envelope.OriginSignPublicKey.ToArray();
        row.OriginSignature = envelope.OriginSignature.ToArray();
        row.EnvelopePayload = serialized;
        row.Status = isRecoveryCandidate
            ? UserSyncSnapshotStatus.RecoveryCandidate
            : UserSyncSnapshotStatus.Pending;
        row.QuarantineReason = null;
        row.ConflictingSnapshotHash = null;

        if (existing is null)
            await _snapshots.AddAsync(row, ct);
        else
            _snapshots.Update(row);

        var isNewKnowledge = knowledge is null;
        knowledge ??= new UserRevisionKnowledge
        {
            UserId = envelope.UserId,
            OriginDeviceId = envelope.OriginDeviceId,
            OriginInstanceId = envelope.OriginInstanceId,
            UserKeyEpoch = envelope.UserKeyEpoch
        };
        if (envelope.OriginRevision > knowledge.HighestStoredRevision ||
            (envelope.OriginRevision == knowledge.HighestStoredRevision &&
             knowledge.HighestStoredSnapshotHash.Length == 0))
        {
            knowledge.HighestStoredRevision = envelope.OriginRevision;
            knowledge.HighestStoredSnapshotHash = envelope.SnapshotHash.ToArray();
        }
        knowledge.LastUpdatedAtUtc = now;
        if (isNewKnowledge)
            await _knowledge.AddAsync(knowledge, ct);
        else
            _knowledge.Update(knowledge);

        return Receipt(
            envelope,
            existing is null ? UserSnapshotReceiptState.StoredPending : UserSnapshotReceiptState.ReplacedOlderPending);
    }

    private async Task RecordForkAsync(
        UserSnapshotEnvelope envelope,
        string diagnosticCode,
        byte[] observedHash,
        byte[] conflictingHash,
        CancellationToken ct)
    {
        if (_syncFaults is null)
            return;
        await _syncFaults.RecordAsync(new UserSyncFaultDescriptor
        {
            UserId = envelope.UserId,
            Scope = UserSyncFaultScope.SnapshotFork,
            Kind = diagnosticCode == "revision-rollback"
                ? UserSyncFaultKind.RevisionRollback
                : diagnosticCode == "duplicate-local-origin"
                    ? UserSyncFaultKind.DuplicateOriginInstallation
                    : UserSyncFaultKind.SameRevisionFork,
            Status = UserSyncHealthStatus.TerminalConflict,
            AffectedComponent = "snapshot-envelope",
            OriginDeviceId = envelope.OriginDeviceId,
            OriginInstanceId = envelope.OriginInstanceId,
            KeyEpoch = envelope.UserKeyEpoch,
            MembershipEpoch = envelope.MembershipEpoch,
            OriginRevision = envelope.OriginRevision,
            ObservedHash = observedHash,
            ConflictingHash = conflictingHash,
            DiagnosticCode = diagnosticCode,
            BlocksMerge = true,
            BlocksLogin = true,
            BlocksGarbageCollection = true
        }, ct);
    }

    private async Task<UserSyncSnapshot> CreateQuarantinedEvidenceAsync(
        UserSnapshotEnvelope envelope,
        Guid transportPeerDeviceId,
        byte[] conflictingHash,
        string reason,
        CancellationToken ct)
    {
        var serialized = SerializeEnvelope(envelope);
        var row = new UserSyncSnapshot
        {
            UserId = envelope.UserId,
            OriginDeviceId = envelope.OriginDeviceId,
            OriginInstanceId = envelope.OriginInstanceId,
            OriginRevision = envelope.OriginRevision,
            UserKeyEpoch = envelope.UserKeyEpoch,
            MembershipEpoch = envelope.MembershipEpoch,
            CreatedAtUtc = envelope.CreatedAtUtc,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            LastReceivedFromDeviceId = transportPeerDeviceId,
            SnapshotHash = envelope.SnapshotHash.ToArray(),
            OriginSignPublicKey = envelope.OriginSignPublicKey.ToArray(),
            OriginSignature = envelope.OriginSignature.ToArray(),
            EnvelopePayload = serialized,
            Status = UserSyncSnapshotStatus.IsolatedFork,
            QuarantineReason = reason,
            ConflictingSnapshotHash = conflictingHash.ToArray()
        };
        await _snapshots.AddAsync(row, ct);
        return row;
    }

    private void Quarantine(
        UserSyncSnapshot row,
        byte[] conflictingHash,
        Guid transportPeerDeviceId,
        string reason)
    {
        row.Status = UserSyncSnapshotStatus.IsolatedFork;
        row.QuarantineReason = reason;
        row.ConflictingSnapshotHash = conflictingHash.ToArray();
        row.ReceivedAtUtc = DateTimeOffset.UtcNow;
        row.LastReceivedFromDeviceId = transportPeerDeviceId;
        _snapshots.Update(row);
    }

    private byte[] SerializeEnvelope(UserSnapshotEnvelope envelope)
    {
        var serialized = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            BackendJsonSerializerContext.Default.UserSnapshotEnvelope);
        if (serialized.Length == 0 || serialized.Length > Constants.SyncConstants.MaxUserSnapshotEnvelopeBytes)
            throw new InvalidDataException("The snapshot envelope size is invalid.");
        return serialized;
    }

    private UserSnapshotEnvelope DeserializeRetainedEnvelope(UserSyncSnapshot row)
    {
        if (row.EnvelopePayload.Length == 0 ||
            row.EnvelopePayload.Length > Constants.SyncConstants.MaxUserSnapshotEnvelopeBytes)
        {
            throw new InvalidDataException("The retained merged-coverage receipt has an invalid envelope size.");
        }

        var envelope = JsonSerializer.Deserialize(
                           row.EnvelopePayload,
                           BackendJsonSerializerContext.Default.UserSnapshotEnvelope)
                       ?? throw new InvalidDataException("The retained merged-coverage receipt is invalid.");
        UserSnapshotEnvelopeUtil.ValidateStructureAndHash(envelope);
        if (row.UserId != envelope.UserId ||
            row.OriginDeviceId != envelope.OriginDeviceId ||
            row.OriginInstanceId != envelope.OriginInstanceId ||
            row.OriginRevision != envelope.OriginRevision ||
            row.UserKeyEpoch != envelope.UserKeyEpoch ||
            row.MembershipEpoch != envelope.MembershipEpoch ||
            row.CreatedAtUtc != envelope.CreatedAtUtc ||
            !Hashing.Verify(row.SnapshotHash, envelope.SnapshotHash) ||
            !Hashing.Verify(row.OriginSignPublicKey, envelope.OriginSignPublicKey) ||
            !Hashing.Verify(row.OriginSignature, envelope.OriginSignature))
        {
            throw new InvalidDataException("The retained merged-coverage receipt row conflicts with its immutable envelope.");
        }

        return envelope;
    }

    private bool CoverageDominates(
        UserSnapshotEnvelope candidate,
        UserSnapshotEnvelope retained)
    {
        if (candidate.UserId != retained.UserId ||
            candidate.OriginDeviceId != retained.OriginDeviceId ||
            candidate.OriginInstanceId != retained.OriginInstanceId ||
            candidate.UserKeyEpoch != retained.UserKeyEpoch ||
            candidate.OriginRevision <= retained.OriginRevision ||
            candidate.MembershipEpoch < retained.MembershipEpoch)
        {
            return false;
        }

        return retained.Coverage.All(entry => Covers(candidate, entry));
    }

    private bool Covers(
        UserSnapshotEnvelope envelope,
        UserSnapshotCoverageEntry reference)
    {
        if (envelope.OriginDeviceId == reference.OriginDeviceId &&
            envelope.OriginInstanceId == reference.OriginInstanceId &&
            envelope.UserKeyEpoch == reference.UserKeyEpoch &&
            envelope.OriginRevision >= reference.OriginRevision)
        {
            return true;
        }

        return envelope.Coverage.Any(entry =>
            entry.OriginDeviceId == reference.OriginDeviceId &&
            entry.OriginInstanceId == reference.OriginInstanceId &&
            entry.UserKeyEpoch == reference.UserKeyEpoch &&
            entry.OriginRevision >= reference.OriginRevision);
    }

    private UserSnapshotReceiptResult Receipt(
        UserSnapshotEnvelope envelope,
        UserSnapshotReceiptState state,
        string? detail = null) =>
        new(
            envelope.UserId,
            envelope.OriginDeviceId,
            envelope.OriginInstanceId,
            envelope.OriginRevision,
            envelope.SnapshotHash.ToArray(),
            state,
            detail);
}
