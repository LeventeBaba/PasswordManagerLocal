using System.Text.Json;
using PasswordManagerLocal.Common.Backend.Diagnostics;
using PasswordManagerLocal.Common.Backend.Internal.Recovery;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed partial class SelfDiagnosticsService
{
    private async Task<bool> TryMergeKnownPendingAsync(User user, CancellationToken ct)
    {
        var pending = (await _snapshots.ListForUserAsync(user.UId, ct)).Where(snapshot => snapshot.UserKeyEpoch == user.KeyEpoch &&
            snapshot.Status is UserSyncSnapshotStatus.Pending or UserSyncSnapshotStatus.RecoveryCandidate).ToArray();
        if (pending.Length == 0) return false;
        try
        {
            foreach (var row in pending)
            {
                var envelope = JsonSerializer.Deserialize(row.EnvelopePayload, BackendJsonSerializerContext.Default.UserSnapshotEnvelope);
                if (envelope is null || envelope.UserId != user.UId || envelope.UserKeyEpoch != user.KeyEpoch ||
                    envelope.MembershipEpoch > user.MembershipEpoch || envelope.OriginDeviceId != row.OriginDeviceId ||
                    envelope.OriginInstanceId != row.OriginInstanceId || envelope.OriginRevision != row.OriginRevision ||
                    !Hashing.Verify(row.SnapshotHash, envelope.SnapshotHash)) return false;
                await _membershipVerifier.VerifySnapshotAuthorAsync(envelope, ct);
            }
            if (!_keyResolver.TryResolve(user, out var key, out var confidence) || key is null) return false;
            using (key) return await _snapshotMerge.TryMergePendingUnderLifecycleAsync(user.UId, key, confidence, ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or OverflowException)
        {
            // Preserve unresolved evidence. A local history problem must not become
            // a false accusation or automatic penalty against a connected device.
            BackendDebugLog.Warning($"Pending evidence needs recovery review. UserId={user.UId}.", ex, "SelfDiagnostics");
            _uow.ClearTrackedChanges();
            return false;
        }
    }

    private async Task<SelfDiagnosticsRecoveryPlan> AnalyzeRecoveryAsync(Guid token, User user, CancellationToken ct)
    {
        var rows = await _authorizations.ListForUserAsync(user.UId, ct);
        var links = await _userDevices.ListByUserAsync(user.UId, ct);
        var local = await _localLinks.GetAsync(user.UId, ct);
        var archive = new UserRecoveryJournalSnapshot
        {
            User = new DeviceEnrollmentUserSnapshot
            {
                UId = user.UId, UsernameHash = user.UsernameHash.ToArray(), UsernameSalt = user.UsernameSalt.ToArray(),
                PasswordSalt = user.PasswordSalt.ToArray(), GeneralUserDataVersion = user.GetGeneralUserDataVersion(),
                EncryptedPayload = user.EncryptedPayload.ToArray(),
                EncryptedGeneralUserDataPayload = user.EncryptedGeneralUserDataPayload.ToArray(),
                EncryptedUserPasswordsDataPayload = user.EncryptedUserPasswordsDataPayload.ToArray(),
                EncryptedUserDevicesDataPayload = user.EncryptedUserDevicesDataPayload.ToArray(),
                KeyEpoch = user.KeyEpoch, MembershipEpoch = user.MembershipEpoch,
                LastModifiedAt = user.LastModifiedAt, UserDataLastModifiedAt = user.UserDataLastModifiedAt,
                GeneralUserDataLastModifiedAt = user.GeneralUserDataLastModifiedAt,
                UserPasswordsDataLastModifiedAt = user.UserPasswordsDataLastModifiedAt,
                UserDevicesDataLastModifiedAt = user.UserDevicesDataLastModifiedAt, IntegrityHash = user.IntegrityHash.ToArray()
            },
            SavedKey = user.SavedKey?.ToArray(), HasLocalLink = local is not null,
            LocalDeviceIdentityId = local?.LocalDeviceIdentityId ?? Guid.Empty,
            LocalSyncOn = local?.IsSyncOn ?? false, LocalLinkIntegrityHash = local?.IntegrityHash.ToArray() ?? [],
            Authorizations = rows.OrderBy(row => row.AuthorizationId).ToList(),
            DeviceLinks = links.OrderBy(link => link.DeviceId).Select(link => new DeviceEnrollmentUserDeviceSnapshot
            {
                UserId = link.UserId, DeviceId = link.DeviceId, IsSyncOn = link.IsSyncOn,
                IsDeleted = link.IsDeleted, DeletedAt = link.DeletedAt, LastModifiedAt = link.LastModifiedAt,
                IntegrityHash = link.IntegrityHash.ToArray()
            }).ToList(),
            Operations = (await _operations.ListForUserAsync(user.UId, ct)).OrderBy(row => row.OperationId).ToList(),
            Cutoffs = (await _cutoffs.ListForUserAsync(user.UId, ct)).OrderBy(row => row.CutoffId).ToList(),
            Snapshots = (await _snapshots.ListForUserAsync(user.UId, ct)).OrderBy(row => row.Id).ToList(),
            Knowledge = (await _knowledge.ListForUserAsync(user.UId, ct)).OrderBy(row => row.OriginDeviceId)
                .ThenBy(row => row.OriginInstanceId).ThenBy(row => row.UserKeyEpoch).ToList(),
            EnrollmentCommits = (await _enrollmentCommits.ListForUserAsync(user.UId, ct)).OrderBy(row => row.CommitId).ToList(),
            Faults = (await _faults.ListForUserAsync(user.UId, ct)).OrderBy(row => row.Id).ToList(),
            ControlState = await _states.GetAsync(user.UId, ct), SyncState = await _syncStates.GetAsync(user.UId, ct),
            Checkpoint = await _checkpoints.GetAsync(user.UId, ct)
        };
        var plan = new SelfDiagnosticsRecoveryPlan { Archive = archive };
        if (await _deletionBarriers.ExistsAsync(user.UId, ct) || archive.Operations.Any(operation =>
            operation.OperationType == UserControlOperationType.AccountDeletion && operation.Status != UserControlOperationStatus.Rejected))
            plan.Blockers.Add("RecoveryBlockedAccountDeletion");

        var peers = rows.Where(row => row.IsActive && row.DeviceId != _identity.LocalDeviceId).Select(row => row.DeviceId)
            .Concat(links.Where(link => !link.IsDeleted && link.DeviceId != _identity.LocalDeviceId).Select(link => link.DeviceId)).ToHashSet();
        try
        {
            using var bundle = await _userDataReader.GetAndVerifyUserDataBundleAsync(user, token, ct);
            try { UserLoginIdentityMetadataUtil.Verify(user, bundle.GeneralUserData); }
            catch (InvalidDataException) { plan.Blockers.Add("RecoveryBlockedLoginMetadata"); }
            foreach (var device in bundle.UserDevicesData.Devices.Where(device => device.Id != _identity.LocalDeviceId))
                if (!links.Any(link => link.DeviceId == device.Id && link.IsDeleted && link.IsIntegrityValid()) &&
                    !bundle.UserDevicesData.DeletedDevices.Any(deleted => deleted.Id == device.Id))
                    peers.Add(device.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            plan.Blockers.Add("RecoveryBlockedDataVerification");
            BackendDebugLog.Warning($"Recovery cannot verify encrypted profile data. UserId={user.UId}.", ex, "SelfDiagnostics");
        }
        plan.AffectedDevices = peers.Count;
        if (peers.Count > 0) plan.Risks.Add("RecoveryRiskOtherDevices");
        if (links.Any(link => !link.IsIntegrityValid())) plan.Risks.Add("RecoveryRiskDeviceRecords");
        var own = rows.Where(row => row.IsActive && row.DeviceId == _identity.LocalDeviceId && row.OriginInstanceId == _identity.OriginInstanceId).ToArray();
        var localMatches = false;
        try
        {
            localMatches = local is not null && local.IsIntegrityValid() && local.LocalDeviceIdentityId == _identity.LocalDeviceId &&
                own.Length == 1 && own[0].AuthorizationId != Guid.Empty && own[0].MinimumKeyEpoch > 0 &&
                own[0].MinimumKeyEpoch <= user.KeyEpoch && own[0].MaximumKeyEpoch is null &&
                own[0].SignPublicKey.SequenceEqual(_identity.SignPublicKey) &&
                Hashing.Verify(own[0].SignPublicKeyHash, Hashing.SHA256Hash(_identity.SignPublicKey)) &&
                Hashing.Verify(own[0].AgreementPublicKeyHash, Hashing.SHA256Hash(_identity.AgreementPublicKey)) &&
                FingerprintsMatch(own[0].TlsCertFingerprint, _identity.FingerprintHex) &&
                own[0].DeviceType == _identity.DeviceType;
        }
        catch (ArgumentException) { }
        if (!localMatches) plan.Risks.Add("RecoveryRiskLocalMembership");
        if (archive.Operations.Any(operation => operation.Status == UserControlOperationStatus.StoredPending))
            plan.Risks.Add("RecoveryRiskPendingOperations");
        var state = archive.ControlState;
        var membershipConflict = archive.Operations.Any(operation => operation.OperationId == state?.ConflictingOperationId &&
            operation.OperationType is UserControlOperationType.DeviceAddition or UserControlOperationType.DeviceRemoval);
        var activeFaults = archive.Faults.Where(fault => fault.Status is not (UserSyncHealthStatus.Recovered or UserSyncHealthStatus.Superseded or UserSyncHealthStatus.Healthy)).ToArray();
        if ((state?.HasConflict == true && !membershipConflict) ||
            archive.Operations.Any(operation => operation.OperationType == UserControlOperationType.KeyEpochReplacement &&
                operation.Status is not (UserControlOperationStatus.Applied or UserControlOperationStatus.Rejected)) ||
            activeFaults.Any(fault => fault.Scope == UserSyncFaultScope.ControlPlane && fault.Kind != UserSyncFaultKind.MembershipConflict))
            plan.Risks.Add("RecoveryRiskControlConflict");
        if (activeFaults.Any(fault => fault.Scope is not (UserSyncFaultScope.Database or UserSyncFaultScope.ControlPlane) &&
            (fault.BlocksPublishing || fault.BlocksMerge || fault.BlocksLogin || fault.BlocksGarbageCollection || fault.BlocksLifecycle)))
            plan.Risks.Add("RecoveryRiskSyncFault");
        if (activeFaults.Any(fault => fault.Scope == UserSyncFaultScope.Database))
            plan.Blockers.Add("RecoveryBlockedDatabase");
        plan.PendingSnapshots = archive.Snapshots.Count(snapshot => snapshot.Status is UserSyncSnapshotStatus.Pending or
            UserSyncSnapshotStatus.RecoveryCandidate or UserSyncSnapshotStatus.IsolatedFork);
        if (plan.PendingSnapshots > 0 || archive.Knowledge.Any(item => item.HighestStoredRevision > item.HighestMergedRevision))
            plan.Risks.Add("RecoveryRiskUnmergedData");
        if (archive.EnrollmentCommits.Any(commit => (commit.Status is DeviceEnrollmentCommitStatus.PendingTransfer or DeviceEnrollmentCommitStatus.TransferFailed) &&
            !links.Any(link => link.DeviceId == commit.TargetDeviceId && link.IsDeleted && link.IsIntegrityValid())))
            plan.Risks.Add("RecoveryRiskEnrollment");
        if (!_keyResolver.TryResolve(user, out var key, out var confidence) || key is null)
            plan.Blockers.Add("RecoveryBlockedKeyUnavailable");
        else
        {
            using (key)
            {
                var health = await _canonicalHealth.VerifyAsync(user, key, confidence, recordFault: false, ct: ct);
                if (!health.CheckpointVerified) plan.Risks.Add("RecoveryRiskCheckpoint");
            }
        }
        plan.MaximumKeyEpoch = archive.Operations.SelectMany(operation => new[] { operation.PreviousKeyEpoch, operation.ResultingKeyEpoch })
            .Concat(rows.SelectMany(row => new[] { row.MinimumKeyEpoch, row.MaximumKeyEpoch ?? 0 }))
            .Concat(archive.Snapshots.Select(row => row.UserKeyEpoch)).Concat(archive.Knowledge.Select(row => row.UserKeyEpoch))
            .Concat(archive.Cutoffs.Select(row => row.UserKeyEpoch)).Concat(archive.Faults.Select(row => row.KeyEpoch ?? 0))
            .Append(state?.AppliedKeyEpoch ?? 0).Append(user.KeyEpoch).Max();
        plan.HighestLocalSequence = archive.Operations.Where(operation => operation.OriginDeviceId == _identity.LocalDeviceId &&
            operation.OriginInstanceId == _identity.OriginInstanceId).Select(operation => operation.OriginSequence).DefaultIfEmpty(0).Max();
        // Corrupt row metadata must not hide a higher, genuinely signed namespace,
        // or permanently veto recovery by claiming an unsigned maximum counter.
        var signedKeyEpoch = user.KeyEpoch;
        var signedSequence = 0L;
        if (archive.Checkpoint is { } checkpoint)
        {
            try
            {
                UserCanonicalCheckpointUtil.VerifyAuthenticity(checkpoint, user.UId, _identity);
                signedKeyEpoch = Math.Max(signedKeyEpoch, checkpoint.KeyEpoch);
            }
            catch (InvalidDataException) { }
        }
        byte[]? ResolveRetainedSigningKey(Guid deviceId, Guid originId)
        {
            if (deviceId == _identity.LocalDeviceId && originId == _identity.OriginInstanceId)
                return _identity.SignPublicKey;
            var matching = rows.Where(row => row.DeviceId == deviceId && row.OriginInstanceId == originId &&
                Hashing.Verify(row.SignPublicKeyHash, Hashing.SHA256Hash(row.SignPublicKey))).Take(2).ToArray();
            return matching.Length == 1 ? matching[0].SignPublicKey : null;
        }
        foreach (var operation in archive.Operations)
        {
            try
            {
                var envelope = UserControlOperationEnvelopeUtil.Deserialize(operation.EnvelopePayload);
                if (envelope.UserId != user.UId) continue;
                var signingKey = ResolveRetainedSigningKey(envelope.OriginDeviceId, envelope.OriginInstanceId);
                if (signingKey is null) continue;
                UserControlOperationEnvelopeUtil.VerifyWithSigningKey(envelope, signingKey);
                signedKeyEpoch = Math.Max(signedKeyEpoch, Math.Max(envelope.PreviousKeyEpoch, envelope.ResultingKeyEpoch));
                if (envelope.OriginDeviceId == _identity.LocalDeviceId && envelope.OriginInstanceId == _identity.OriginInstanceId)
                    signedSequence = Math.Max(signedSequence, envelope.OriginSequence);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or OverflowException) { }
        }
        foreach (var snapshot in archive.Snapshots)
        {
            try
            {
                var envelope = JsonSerializer.Deserialize(snapshot.EnvelopePayload, BackendJsonSerializerContext.Default.UserSnapshotEnvelope);
                if (envelope is null || envelope.UserId != user.UId) continue;
                var signingKey = ResolveRetainedSigningKey(envelope.OriginDeviceId, envelope.OriginInstanceId);
                if (signingKey is null) continue;
                UserSnapshotEnvelopeUtil.VerifyWithSigningKey(envelope, signingKey);
                signedKeyEpoch = Math.Max(signedKeyEpoch, envelope.UserKeyEpoch);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or OverflowException) { }
        }
        if ((plan.MaximumKeyEpoch == long.MaxValue && signedKeyEpoch < long.MaxValue) ||
            (plan.HighestLocalSequence >= long.MaxValue - 1 && signedSequence < long.MaxValue - 1) ||
            (state is not null && state.NextOriginSequence >= long.MaxValue - 1) ||
            (archive.SyncState is { } counterSync && (counterSync.NextOriginRevision <= 0 || counterSync.NextOriginRevision >= long.MaxValue - 1)) ||
            archive.EnrollmentCommits.Any(commit => commit.Version < 0 || commit.Version == long.MaxValue) ||
            (archive.Checkpoint is { } counterCheckpoint && (counterCheckpoint.CheckpointSequence <= 0 || counterCheckpoint.CheckpointSequence >= long.MaxValue - 1)))
            plan.Risks.Add("RecoveryRiskCounterMetadata");
        plan.MaximumKeyEpoch = plan.MaximumKeyEpoch == long.MaxValue && signedKeyEpoch < long.MaxValue
            ? signedKeyEpoch : Math.Max(plan.MaximumKeyEpoch, signedKeyEpoch);
        plan.HighestLocalSequence = plan.HighestLocalSequence >= long.MaxValue - 1 && signedSequence < long.MaxValue - 1
            ? signedSequence : Math.Max(plan.HighestLocalSequence, signedSequence);
        if (plan.MaximumKeyEpoch == long.MaxValue || plan.HighestLocalSequence >= long.MaxValue - 1)
            plan.Blockers.Add("RecoveryBlockedEpochExhausted");
        UtcDateTimeUtil.NormalizeObjectGraph(archive);
        // Retain the exact archive for the journal, but do not expire approval when
        // background services only re-observe the same evidence or reschedule a retry.
        plan.PlanId = SelfDiagnosticsRecoveryPlanFingerprint.Calculate(plan, token, _identity.LocalDeviceId, _identity.OriginInstanceId);
        foreach (var code in plan.Blockers.Concat(plan.Risks))
            BackendDebugLog.Info($"Recovery assessment. UserId={user.UId}, Reason={code}.", "SelfDiagnostics");
        return plan;
    }
}
