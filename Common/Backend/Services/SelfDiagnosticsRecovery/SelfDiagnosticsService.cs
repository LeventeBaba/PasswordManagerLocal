using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Diagnostics;
using PasswordManagerLocal.Common.Backend.Models;
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

    private static SelfDiagnosticsResultResponse WithRepair(SelfDiagnosticsResultResponse result, int count, string code) => new()
    {
        Healthy = result.Healthy,
        RepairedCount = result.RepairedCount + count,
        Findings = result.Findings.Append(code).Distinct().ToArray()
    };

    private async Task ReplaceRemovalCutoffsAsync(
        IReadOnlyList<UserOriginRemovalCutoff> existing,
        IReadOnlyList<UserOriginRemovalCutoff> expected,
        CancellationToken ct)
    {
        await using var transaction = await _uow.BeginTransactionAsync(ct);
        try
        {
            _cutoffs.DeleteRange(existing);
            // Delete first so replacing a damaged namespace cannot violate its unique index.
            await _uow.SaveChangesAsync(ct);
            foreach (var cutoff in expected)
                await _cutoffs.AddAsync(cutoff, ct);
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

    private async Task<SelfDiagnosticsResultResponse> CompleteHistoryRepairAsync(
        Guid token, User user, HashSet<string> findings, bool allowRepair, CancellationToken ct)
    {
        if (allowRepair && !findings.Contains("LocalMembershipInvalid") &&
            findings.Any(code => code is "SignedRemovalHistoryInvalid" or "RemovalCutoffInvalid" or
                "RemovalCutoffMissing" or "MembershipHistoryInvalid" or "GenesisHistoryInvalid"))
        {
            if (await TryResetSingleDeviceHistoryAsync(token, user, ct))
            {
                var verified = await RunUnderLifecycleAsync(token, allowRepair: false, ct);
                return WithRepair(verified, 1, "SingleDeviceHistoryReset");
            }
            findings.Add("HistoryResetNotSafe");
        }
        return new SelfDiagnosticsResultResponse { Healthy = false, Findings = findings.ToArray() };
    }

    private async Task<bool> TryResetSingleDeviceHistoryAsync(Guid token, User user, CancellationToken ct)
    {
        // All reads and writes run under the same per-profile lifecycle lease. A blocked
        // peer, a sync-disabled peer, or a device belonging to a second profile is NOT
        // evidence that this profile has no other devices.
        var rows = await _authorizations.ListForUserAsync(user.UId, ct);
        var active = rows.Where(row => row.IsActive).ToArray();
        if (active.Length != 1 || active[0].DeviceId != _identity.LocalDeviceId ||
            active[0].OriginInstanceId != _identity.OriginInstanceId ||
            active[0].AuthorizationId == Guid.Empty ||
            await _deletionBarriers.ExistsAsync(user.UId, ct))
            return false;
        var own = active[0];
        var links = await _userDevices.ListByUserAsync(user.UId, ct);
        if (links.Any(link => link.DeviceId != _identity.LocalDeviceId &&
                              (!link.IsDeleted || !link.IsIntegrityValid())))
            return false;

        // Read and verify all decrypted content before discarding any history. No
        // password, tag, colour, name, encryption key, or identity is recreated.
        using var bundle = await _userDataReader.GetAndVerifyUserDataBundleAsync(user, token, ct);
        if (bundle.UserDevicesData.Devices.Any(device => device.Id != _identity.LocalDeviceId &&
            !links.Any(link => link.DeviceId == device.Id && link.IsDeleted && link.IsIntegrityValid()) &&
            !bundle.UserDevicesData.DeletedDevices.Any(deleted => deleted.Id == device.Id)))
            return false;

        var operations = await _operations.ListForUserAsync(user.UId, ct);
        if (operations.Any(operation => operation.OperationType == UserControlOperationType.AccountDeletion ||
            operation.Status == UserControlOperationStatus.StoredPending ||
            (operation.OperationType == UserControlOperationType.KeyEpochReplacement &&
             operation.Status != UserControlOperationStatus.Applied)))
            return false;
        var state = await _states.GetAsync(user.UId, ct);
        if (state?.HasConflict == true &&
            !operations.Any(operation => operation.OperationId == state.ConflictingOperationId &&
                operation.OperationType is UserControlOperationType.DeviceAddition or UserControlOperationType.DeviceRemoval))
            return false;
        var snapshots = await _snapshots.ListForUserAsync(user.UId, ct);
        var knowledge = await _knowledge.ListForUserAsync(user.UId, ct);
        if (snapshots.Any(snapshot => snapshot.Status is UserSyncSnapshotStatus.Pending or
                UserSyncSnapshotStatus.RecoveryCandidate or UserSyncSnapshotStatus.IsolatedFork) ||
            knowledge.Any(item => item.HighestStoredRevision > item.HighestMergedRevision))
            return false; // Never discard unmerged changes or fork evidence to make diagnostics green.
        var faults = await _faults.ListActiveForUserAsync(user.UId, ct);
        if (faults.Any(fault => fault.Scope == UserSyncFaultScope.ControlPlane &&
                               fault.Kind != UserSyncFaultKind.MembershipConflict))
            return false;
        var commits = await _enrollmentCommits.ListForUserAsync(user.UId, ct);
        if (commits.Any(commit => (commit.Status is DeviceEnrollmentCommitStatus.PendingTransfer or
                DeviceEnrollmentCommitStatus.TransferFailed) &&
                !links.Any(link => link.DeviceId == commit.TargetDeviceId && link.IsDeleted && link.IsIntegrityValid())))
            return false;

        if (!_keyResolver.TryResolve(user, out var key, out var confidence) || key is null)
            return false;
        using (key)
        {
            if (!(await _canonicalHealth.VerifyAsync(user, key, confidence, recordFault: false, ct: ct)).FullyVerified)
                return false;
        }

        // Start above every retained key namespace. Merely setting membership to 1
        // would let an old, validly signed local addition/removal be replayed.
        // This advances the causal namespace; it does not change the encryption key.
        var cutoffs = await _cutoffs.ListForUserAsync(user.UId, ct);
        var maximumKeyEpoch = operations.SelectMany(operation => new[] { operation.PreviousKeyEpoch, operation.ResultingKeyEpoch })
            .Concat(rows.Select(row => row.MaximumKeyEpoch ?? row.MinimumKeyEpoch))
            .Concat(snapshots.Select(snapshot => snapshot.UserKeyEpoch))
            .Concat(knowledge.Select(item => item.UserKeyEpoch))
            .Concat(cutoffs.Select(cutoff => cutoff.UserKeyEpoch))
            .Concat(faults.Select(fault => fault.KeyEpoch ?? 0))
            .Append(state?.AppliedKeyEpoch ?? 0).Append(user.KeyEpoch).Max();
        if (maximumKeyEpoch == long.MaxValue)
            return false;
        var highestSequence = operations.Where(operation => operation.OriginDeviceId == _identity.LocalDeviceId &&
                operation.OriginInstanceId == _identity.OriginInstanceId)
            .Select(operation => operation.OriginSequence).DefaultIfEmpty(0).Max();
        if (highestSequence == long.MaxValue)
            return false;
        await using var transaction = await _uow.BeginTransactionAsync(ct);
        try
        {
            _cutoffs.DeleteRange(cutoffs);
            _operations.DeleteRange(operations);
            _authorizations.DeleteRange(rows.Where(row => row.AuthorizationId != own.AuthorizationId));
            _knowledge.DeleteRange(knowledge);
            // Keep old envelope bytes in the database as evidence, outside the new
            // enrollment/merge baseline. No pending or unmerged rows reach this path.
            foreach (var snapshot in snapshots)
            {
                snapshot.Status = UserSyncSnapshotStatus.SupersededBadEvidence;
                snapshot.QuarantineReason = "single-device-membership-history-reset";
                _snapshots.Update(snapshot);
            }
            foreach (var commit in commits)
            {
                commit.Status = DeviceEnrollmentCommitStatus.Revoked;
                commit.LastError = "single-device-membership-history-reset";
                commit.Version = checked(commit.Version + 1);
                _enrollmentCommits.Update(commit);
            }
            user.KeyEpoch = checked(maximumKeyEpoch + 1);
            user.MembershipEpoch = 1;
            user.GenerateIntegrityHash();
            _userRepository.Update(user);

            own.IsGenesis = true;
            own.StartedMembershipEpoch = 1;
            own.MinimumKeyEpoch = user.KeyEpoch;
            own.MaximumKeyEpoch = null;
            own.EndedMembershipEpoch = null;
            own.AdditionOperationId = null;
            own.AdditionOperationHash = null;
            own.RemovalOperationId = null;
            own.RemovalOperationHash = null;
            own.EndedAtUtc = null;
            own.Version = checked(own.Version + 1);
            _authorizations.Update(own);

            var newState = state is null;
            state ??= new UserControlState { UserId = user.UId };
            state.LocalOriginInstanceId = _identity.OriginInstanceId;
            state.NextOriginSequence = Math.Max(Math.Max(1, state.NextOriginSequence), highestSequence + 1);
            state.AppliedKeyEpoch = user.KeyEpoch;
            state.AppliedMembershipEpoch = 1;
            state.HasConflict = false;
            state.ConflictReason = null;
            state.ConflictingOperationId = null;
            state.ConflictingOperationHash = null;
            state.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            if (newState) await _states.AddAsync(state, ct);
            else _states.Update(state);
            var syncState = await _syncStates.GetAsync(user.UId, ct);
            if (syncState is not null)
            {
                syncState.LastPublishedContentHash = [];
                _syncStates.Update(syncState);
            }
            foreach (var fault in faults.Where(fault => fault.Scope == UserSyncFaultScope.ControlPlane &&
                         fault.Kind == UserSyncFaultKind.MembershipConflict))
            {
                fault.Status = UserSyncHealthStatus.Recovered;
                fault.RecoveredAtUtc = DateTimeOffset.UtcNow;
                fault.NextRecoveryAttemptAtUtc = null;
                fault.BlocksPublishing = false;
                fault.BlocksMerge = false;
                fault.BlocksLogin = false;
                fault.BlocksGarbageCollection = false;
                fault.BlocksLifecycle = false;
                _faults.Update(fault);
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
        BackendDebugLog.Warning($"Reset unrecoverable device history for a verified single-device profile. UserId={user.UId}, NewKeyEpoch={user.KeyEpoch}.", category: "SelfDiagnostics");
        return true;
    }
}
