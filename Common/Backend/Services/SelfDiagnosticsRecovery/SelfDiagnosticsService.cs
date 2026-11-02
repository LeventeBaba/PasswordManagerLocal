using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Diagnostics;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Internal.Recovery;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed partial class SelfDiagnosticsService
{
    private readonly IUserRepository _userRepository;
    private readonly IUnitOfWork _uow;
    private readonly IUserRevisionKnowledgeRepository _knowledge;
    private readonly IUserSyncSnapshotRepository _snapshots;
    private readonly IUserSyncStateRepository _syncStates;
    private readonly IDeviceEnrollmentCommitRepository _enrollmentCommits;
    private readonly IUserSyncKeyResolverService _keyResolver;
    private readonly IUserCanonicalHealthService _canonicalHealth;
    private readonly IUserLoginIdentityProjectionService _loginIdentities;
    private readonly IDeletedUserBarrierRepository _deletionBarriers;
    private readonly IUserSyncFaultRepository _faults;
    private readonly IUserRecoveryJournalService _recoveryJournal;
    private readonly IUserCanonicalCheckpointRepository _checkpoints;
    private readonly IUserSnapshotMergeCoordinator _snapshotMerge;
    private readonly IUserMembershipAuthorizationService _membershipVerifier;
    private readonly ICredentialVerificationService _credentialVerification;

    private static SelfDiagnosticsResultResponse WithRepair(SelfDiagnosticsResultResponse result, int count, string code) => new()
    {
        Healthy = result.Healthy, RepairedCount = result.RepairedCount + count,
        Findings = new[] { code }.Concat(result.Findings).Distinct().Take(32).ToArray(),
        RecoveryPlanId = result.RecoveryPlanId, RecoveryRisks = result.RecoveryRisks,
        RecoveryAffectedDevices = result.RecoveryAffectedDevices, RecoveryPendingSnapshots = result.RecoveryPendingSnapshots,
        RecoveryJournalId = result.RecoveryJournalId
    };

    private async Task ReplaceRemovalCutoffsAsync(IReadOnlyList<UserOriginRemovalCutoff> existing,
        IReadOnlyList<UserOriginRemovalCutoff> expected, CancellationToken ct)
    {
        await using var transaction = await _uow.BeginTransactionAsync(ct);
        try
        {
            _cutoffs.DeleteRange(existing);
            await _uow.SaveChangesAsync(ct);
            foreach (var cutoff in expected) await _cutoffs.AddAsync(cutoff, ct);
            await _uow.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _uow.ClearTrackedChanges();
            throw;
        }
    }

    private async Task RepairControlSequenceAsync(UserControlState state, long highest, CancellationToken ct)
    {
        await using var transaction = await _uow.BeginTransactionAsync(ct);
        try
        {
            state.LocalOriginInstanceId = _identity.OriginInstanceId;
            state.NextOriginSequence = Math.Max(1, highest + 1);
            state.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            _states.Update(state);
            await _uow.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _uow.ClearTrackedChanges();
            throw;
        }
    }

    private static SelfDiagnosticsResultResponse DescribeRecovery(SelfDiagnosticsRecoveryPlan plan, IEnumerable<string> findings) => new()
    {
        Healthy = false,
        Findings = findings.Concat(plan.Blockers).Concat(plan.Risks).Distinct().Take(32).ToArray(),
        RecoveryPlanId = plan.CanRecover ? plan.PlanId : null,
        RecoveryRisks = plan.Risks.ToArray(), RecoveryAffectedDevices = plan.AffectedDevices,
        RecoveryPendingSnapshots = plan.PendingSnapshots
    };

    private async Task<SelfDiagnosticsResultResponse> CompleteHistoryRepairAsync(
        Guid token, User user, HashSet<string> findings, bool allowRepair, CancellationToken ct)
    {
        if (!allowRepair) return new SelfDiagnosticsResultResponse { Healthy = false, Findings = findings.ToArray() };
        var plan = await AnalyzeRecoveryAsync(token, user, ct);
        if (plan.CanRecover && plan.Risks.Count == 0)
            return await RecoverProfileAsync(token, user, plan, confirmed: false, ct);
        return DescribeRecovery(plan, findings.Append("HistoryResetNotSafe"));
    }

    public async Task<SelfDiagnosticsResultResponse> ConfirmRepairAsync(Guid token, string planId, byte[] masterPassword, CancellationToken ct = default)
    {
        if (masterPassword is null || masterPassword.Length == 0 || masterPassword.Length > 4096) throw new InvalidInputException();
        if (planId is null || planId.Length != 64 || !planId.All(Uri.IsHexDigit)) throw new InvalidInputException();
        var userId = (await _users.GetAndVerifyUserAsync(token, ct)).UId;
        return await _lifecycle.ExecuteAsync(userId, async inner =>
        {
            var user = await _users.GetAndVerifyUserAsync(token, inner);
            var plan = await AnalyzeRecoveryAsync(token, user, inner);
            if (!plan.CanRecover) return DescribeRecovery(plan, ["HistoryResetNotSafe"]);
            if (!Hashing.Verify(Convert.FromHexString(plan.PlanId), Convert.FromHexString(planId)))
            {
                var current = await RunUnderLifecycleAsync(token, allowRepair: false, inner);
                if (current.Healthy)
                {
                    current.Findings = current.Findings.Append("RecoveryPlanChanged").ToArray();
                    return current;
                }
                return DescribeRecovery(plan, ["RecoveryPlanChanged"]);
            }
            if (!_credentialVerification.IsPasswordValid(token, masterPassword, user.PasswordSalt))
                return DescribeRecovery(plan, ["RecoveryPasswordInvalid"]);
            return await RecoverProfileAsync(token, user, plan, confirmed: true, inner);
        }, ct);
    }

    private async Task<SelfDiagnosticsResultResponse> RecoverProfileAsync(
        Guid token, User user, SelfDiagnosticsRecoveryPlan plan, bool confirmed, CancellationToken ct)
    {
        Guid journalId;
        try { journalId = await ApplyHistoryResetAsync(user, plan, confirmed, ct); }
        catch (RecoveryJournalPersistenceException ex)
        {
            BackendDebugLog.Warning($"Recovery journal could not be verified. History was not reset. UserId={user.UId}.", ex, "SelfDiagnostics");
            plan.Blockers.Add("RecoveryBlockedJournal");
            return DescribeRecovery(plan, ["HistoryResetNotSafe"]);
        }
        try
        {
            if (confirmed) await RepairRemovedDeviceMetadataAsync(token, user.UId, ct, includeUnprovenRemoteMetadata: true);
            var verified = await RunUnderLifecycleAsync(token, allowRepair: false, ct);
            if (verified.Healthy)
            {
                verified.RecoveryJournalId = await _recoveryJournal.CompleteAsync(journalId, ct) ? journalId : null;
                verified.Findings = verified.Findings.Append(verified.RecoveryJournalId is null ? "RecoveryJournalCleaned" : "RecoveryJournalRetained").ToArray();
            }
            else verified.RecoveryJournalId = journalId;
            return WithRepair(verified, 1, confirmed ? "ConfirmedHistoryRecovery" : "SingleDeviceHistoryReset");
        }
        catch (Exception ex)
        {
            throw new MutationPartiallyCommittedException(
                $"Profile history was recovered, but follow-up verification or cleanup did not complete. Recovery journal {journalId:N} remains inside the protected database if still needed.", innerException: ex);
        }
    }

    private async Task<Guid> ApplyHistoryResetAsync(User user, SelfDiagnosticsRecoveryPlan plan, bool confirmed, CancellationToken ct)
    {
        var archive = plan.Archive;
        Guid journalId;
        await using var transaction = await _uow.BeginTransactionAsync(ct);
        try
        {
            // The recovery record and history reset share this transaction. If either fails,
            // both roll back; the original profile remains the recovery source.
            journalId = await StageRecoveryJournalAsync(archive, checked(plan.MaximumKeyEpoch + 1), confirmed, ct);
            // All affected rows belong to this profile. Other profiles and global devices are untouched.
            _cutoffs.DeleteRange(archive.Cutoffs);
            _operations.DeleteRange(archive.Operations);
            _authorizations.DeleteRange(archive.Authorizations);
            _knowledge.DeleteRange(archive.Knowledge);
            await _uow.SaveChangesAsync(ct); // Release exact-installation unique keys before creating the new genesis.
            foreach (var snapshot in archive.Snapshots)
            {
                snapshot.Status = UserSyncSnapshotStatus.SupersededBadEvidence;
                snapshot.QuarantineReason = "profile-device-history-recovery";
                _snapshots.Update(snapshot);
            }
            foreach (var commit in archive.EnrollmentCommits)
            {
                commit.Status = DeviceEnrollmentCommitStatus.Revoked;
                commit.LastError = "profile-device-history-recovery";
                commit.Version = commit.Version < 0 || commit.Version == long.MaxValue ? 1 : commit.Version + 1;
                _enrollmentCommits.Update(commit);
            }
            var now = DateTimeOffset.UtcNow;
            foreach (var source in archive.DeviceLinks.Where(link => link.DeviceId != _identity.LocalDeviceId))
            {
                var link = await _userDevices.GetAsync(user.UId, source.DeviceId, ct);
                if (link is null) continue;
                link.IsDeleted = true; link.DeletedAt = now; link.IsSyncOn = false; link.LastModifiedAt = now;
                _userDevices.Update(link);
            }
            var local = await _localLinks.GetAsync(user.UId, ct);
            var newLocal = local is null;
            local ??= new LocalUserDevice { UserId = user.UId };
            local.LocalDeviceIdentityId = _identity.LocalDeviceId;
            local.IsSyncOn = !confirmed && archive.LocalSyncOn;
            local.GenerateIntegrityHash();
            if (newLocal) await _localLinks.AddAsync(local, ct);
            else _localLinks.Update(local);

            user.KeyEpoch = checked(plan.MaximumKeyEpoch + 1);
            user.MembershipEpoch = 1;
            user.GenerateIntegrityHash();
            _userRepository.Update(user);
            await _authorizations.AddAsync(new UserMembershipAuthorization
            {
                AuthorizationId = Guid.NewGuid(), UserId = user.UId,
                DeviceId = _identity.LocalDeviceId, OriginInstanceId = _identity.OriginInstanceId,
                SignPublicKey = _identity.SignPublicKey.ToArray(), SignPublicKeyHash = Hashing.SHA256Hash(_identity.SignPublicKey),
                AgreementPublicKeyHash = Hashing.SHA256Hash(_identity.AgreementPublicKey),
                TlsCertFingerprint = SyncIdentityUtil.NormalizeFingerprint(_identity.FingerprintHex), DeviceType = _identity.DeviceType,
                StartedMembershipEpoch = 1, MinimumKeyEpoch = user.KeyEpoch, IsActive = true, IsGenesis = true, CreatedAtUtc = now
            }, ct);
            var state = archive.ControlState;
            var newState = state is null;
            state ??= new UserControlState { UserId = user.UId };
            state.LocalOriginInstanceId = _identity.OriginInstanceId;
            state.NextOriginSequence = Math.Max(Math.Max(1, plan.HighestLocalSequence + 1),
                state.NextOriginSequence > 0 && state.NextOriginSequence < long.MaxValue - 1 ? state.NextOriginSequence : 1);
            state.AppliedKeyEpoch = user.KeyEpoch; state.AppliedMembershipEpoch = 1;
            state.HasConflict = false; state.ConflictReason = null;
            state.ConflictingOperationId = null; state.ConflictingOperationHash = null; state.LastUpdatedAtUtc = now;
            if (newState) await _states.AddAsync(state, ct); else _states.Update(state);
            if (archive.SyncState is { } syncState)
            {
                syncState.LastPublishedContentHash = [];
                // Revisions are scoped by key epoch. Unusable old counters can restart
                // inside the fresh epoch; the original evidence remains in the archive.
                syncState.LocalOriginInstanceId = _identity.OriginInstanceId;
                syncState.NextOriginRevision = syncState.NextOriginRevision > 0 && syncState.NextOriginRevision < long.MaxValue - 1
                    ? syncState.NextOriginRevision : 1;
                syncState.LastUpdatedAtUtc = now;
                _syncStates.Update(syncState);
            }
            foreach (var fault in archive.Faults.Where(fault => fault.Scope != UserSyncFaultScope.Database))
            {
                fault.Status = fault.Scope == UserSyncFaultScope.ControlPlane && fault.Kind == UserSyncFaultKind.MembershipConflict
                    ? UserSyncHealthStatus.Recovered : UserSyncHealthStatus.Superseded;
                fault.RecoveredAtUtc = now;
                fault.NextRecoveryAttemptAtUtc = null; fault.BlocksPublishing = false; fault.BlocksMerge = false;
                fault.BlocksLogin = false; fault.BlocksGarbageCollection = false; fault.BlocksLifecycle = false;
                _faults.Update(fault);
            }
            if (confirmed && archive.Checkpoint is { } oldCheckpoint &&
                (oldCheckpoint.CheckpointSequence <= 0 || oldCheckpoint.CheckpointSequence >= long.MaxValue - 1))
            {
                await _canonicalHealth.DeleteCheckpointAsync(user.UId, ct);
                await _uow.SaveChangesAsync(ct);
            }
            await _canonicalHealth.UpdateCheckpointAsync(user, ct);
            await _loginIdentities.SetCanonicalAsync(user, user.GetGeneralUserDataVersion(), ct);
            await _uow.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _uow.ClearTrackedChanges();
            throw;
        }
        BackendDebugLog.Warning($"Recovered device history. UserId={user.UId}, Confirmed={confirmed}, JournalId={archive.JournalId}, NewKeyEpoch={user.KeyEpoch}.", category: "SelfDiagnostics");
        return journalId;
    }
}
