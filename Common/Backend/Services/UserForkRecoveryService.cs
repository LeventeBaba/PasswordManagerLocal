using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Tombstones;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>Proves a deliberately narrow removal of an exact, never-merged remote installation.</summary>
public sealed class UserForkRecoveryService(
    IUserSyncSnapshotRepository snapshots,
    IUserSyncFaultRepository faults,
    IUserRevisionKnowledgeRepository knowledge,
    IUserMembershipAuthorizationRepository authorizations,
    IUserOriginRemovalCutoffRepository cutoffs,
    IUserControlOperationRepository operations,
    IUserControlStateRepository states,
    IUserCanonicalCheckpointRepository checkpoints,
    IUserMembershipAuthorizationService membership,
    IUserSyncFaultService faultWriter,
    IDeletedUserBarrierRepository deletions,
    IDeviceIdentityService identity) : IUserForkRecoveryService
{
    public async Task<DeviceRemovalForkProof> PrepareRemovalAsync(User user, Guid selectedRemoteDeviceId, CancellationToken ct = default)
    {
        if (selectedRemoteDeviceId == Guid.Empty || selectedRemoteDeviceId == identity.LocalDeviceId ||
            user.KeyEpoch != 1 || await deletions.ExistsAsync(user.UId, ct))
            throw Blocked();
        await membership.VerifyLocalInstallationAsync(user.UId, user.KeyEpoch, user.MembershipEpoch, ct);
        var state = await states.GetAsync(user.UId, ct);
        if (state is null || state.HasConflict || state.AppliedKeyEpoch != user.KeyEpoch ||
            state.AppliedMembershipEpoch != user.MembershipEpoch) throw Blocked();
        var members = await authorizations.ListForUserAsync(user.UId, ct);
        var targets = members.Where(row => row.DeviceId == selectedRemoteDeviceId).ToArray();
        if (targets.Length != 1 || !targets[0].IsActive || targets[0].IsGenesis) throw Blocked();
        var target = targets[0];
        await VerifyUncontestedHistoryAsync(user, members, ct);
        var retained = (await snapshots.ListForUserAsync(user.UId, ct))
            .Where(row => row.Status == UserSyncSnapshotStatus.IsolatedFork).ToArray();
        if (retained.Length != 1 || retained[0].OriginDeviceId != target.DeviceId ||
            retained[0].OriginInstanceId != target.OriginInstanceId || retained[0].ConflictingEnvelopePayload is not { Length: > 0 })
            throw Blocked();
        var faultRows = await faults.ListActiveForUserAsync(user.UId, ct);
        if (faultRows.Count == 0 || faultRows.Any(fault => fault.Scope != UserSyncFaultScope.SnapshotFork ||
                fault.Kind != UserSyncFaultKind.SameRevisionFork || fault.Status != UserSyncHealthStatus.TerminalConflict ||
                fault.OriginDeviceId != target.DeviceId || fault.OriginInstanceId != target.OriginInstanceId ||
                fault.KeyEpoch != retained[0].UserKeyEpoch || fault.MembershipEpoch != retained[0].MembershipEpoch ||
                fault.OriginRevision != retained[0].OriginRevision))
            throw Blocked();
        var proof = new DeviceRemovalForkProof
        {
            AuthorizationId = target.AuthorizationId,
            FirstEnvelope = retained[0].EnvelopePayload.ToArray(),
            SecondEnvelope = retained[0].ConflictingEnvelopePayload!.ToArray()
        };
        var payload = CreateProofScope(user, target, proof);
        var (first, second) = DeviceRemovalForkProofUtil.Validate(payload);
        var exact = UserSnapshotEvidenceUtil.DeserializeExact(retained[0]);
        if (!Hashing.Verify(exact.SnapshotHash, first.SnapshotHash) ||
            retained[0].ConflictingSnapshotHash is not { } competingHash || !Hashing.Verify(competingHash, second.SnapshotHash) ||
            faultRows.Any(fault => !((Hashing.Verify(fault.ObservedHash, first.SnapshotHash) && Hashing.Verify(fault.ConflictingHash, second.SnapshotHash)) ||
                                    (Hashing.Verify(fault.ObservedHash, second.SnapshotHash) && Hashing.Verify(fault.ConflictingHash, first.SnapshotHash)))))
            throw Blocked();
        DeviceRemovalForkProofUtil.VerifyAuthorization(payload, target);
        if ((await operations.ListForUserAsync(user.UId, ct)).Any(row => row.OriginDeviceId == target.DeviceId &&
                row.OriginInstanceId == target.OriginInstanceId)) throw Blocked();
        if (!await CanonicalExcludesOriginAsync(user, target, ct)) throw Blocked();
        return proof;
    }

    public async Task<bool> CanAccessIndependentCanonicalAsync(User user, CancellationToken ct = default)
    {
        var terminal = (await faults.ListActiveForUserAsync(user.UId, ct)).Where(UserSyncFaultPolicy.IsTerminal).ToArray();
        var targets = terminal.Select(fault => fault.OriginDeviceId).Distinct().ToArray();
        if (targets.Length != 1 || targets[0] is not Guid target) return false;
        try { await PrepareRemovalAsync(user, target, ct); return true; }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or UnauthorizedAccessException or
                                       System.Text.Json.JsonException or ArgumentException or System.Security.Cryptography.CryptographicException)
        { return false; }
    }

    public async Task StageSignedExclusionAsync(User user, DeviceRemovalPayload payload, UserControlOperationEnvelope operation, CancellationToken ct = default)
    {
        if (payload.ForkExclusion is null) return;
        if (operation.UserId != user.UId || operation.OperationType != UserControlOperationType.DeviceRemoval ||
            operation.PreviousMembershipEpoch != user.MembershipEpoch || payload.PreviousMembershipEpoch != user.MembershipEpoch ||
            !UserControlOperationEnvelopeUtil.SerializeDeviceRemovalPayload(payload).AsSpan().SequenceEqual(operation.OperationPayload))
            throw Blocked();
        await membership.VerifyControlAuthorAsync(operation, ct);
        var target = await authorizations.GetByIdAsync(payload.ForkExclusion.AuthorizationId, ct) ?? throw Blocked();
        DeviceRemovalForkProofUtil.VerifyAuthorization(payload, target);
        var (first, second) = DeviceRemovalForkProofUtil.Validate(payload);
        var uncontaminated = identity.LocalDeviceId != target.DeviceId && await CanonicalExcludesOriginAsync(user, target, ct);
        var existing = await snapshots.GetAsync(user.UId, target.DeviceId, target.OriginInstanceId, first.UserKeyEpoch, ct);
        if (existing is null)
        {
            existing = new UserSyncSnapshot
            {
                UserId = first.UserId, OriginDeviceId = first.OriginDeviceId, OriginInstanceId = first.OriginInstanceId,
                OriginRevision = first.OriginRevision, UserKeyEpoch = first.UserKeyEpoch, MembershipEpoch = first.MembershipEpoch,
                CreatedAtUtc = first.CreatedAtUtc, SnapshotHash = first.SnapshotHash.ToArray(),
                OriginSignPublicKey = first.OriginSignPublicKey.ToArray(), OriginSignature = first.OriginSignature.ToArray(),
                EnvelopePayload = payload.ForkExclusion.FirstEnvelope.ToArray(),
                ConflictingEnvelopePayload = payload.ForkExclusion.SecondEnvelope.ToArray(),
                ConflictingSnapshotHash = second.SnapshotHash.ToArray(),
                QuarantineReason = "signed-fork-origin-exclusion", Status = UserSyncSnapshotStatus.IsolatedFork
            };
            await snapshots.AddAsync(existing, ct);
        }
        // Complete a matching retained original without overwriting an independently
        // observed third branch. The operation always retains the exact signed pair.
        if (existing.OriginRevision == first.OriginRevision && Hashing.Verify(existing.SnapshotHash, first.SnapshotHash) &&
            existing.ConflictingEnvelopePayload is null && (existing.ConflictingSnapshotHash is null ||
                Hashing.Verify(existing.ConflictingSnapshotHash, second.SnapshotHash)))
        {
            existing.ConflictingEnvelopePayload = payload.ForkExclusion.SecondEnvelope.ToArray();
            existing.ConflictingSnapshotHash = second.SnapshotHash.ToArray();
        }
        existing.QuarantineReason ??= "signed-fork-origin-exclusion";
        // An observed third branch is retained as-is. The signed removal itself keeps
        // the original pair even when the receiver's one retained namespace differs.
        if (uncontaminated)
        {
            foreach (var row in (await snapshots.ListForUserAsync(user.UId, ct))
                         .Where(row => row.OriginDeviceId == target.DeviceId && row.OriginInstanceId == target.OriginInstanceId &&
                                       row.Status == UserSyncSnapshotStatus.IsolatedFork))
            {
                row.Status = UserSyncSnapshotStatus.RetiredForkEvidence;
                snapshots.Update(row);
            }
            existing!.Status = UserSyncSnapshotStatus.RetiredForkEvidence;
            snapshots.Update(existing);
            foreach (var fault in (await faults.ListActiveForUserAsync(user.UId, ct)).Where(fault =>
                         fault.OriginDeviceId == target.DeviceId && fault.OriginInstanceId == target.OriginInstanceId &&
                         fault.Scope == UserSyncFaultScope.SnapshotFork && fault.Kind == UserSyncFaultKind.SameRevisionFork))
            {
                fault.Status = UserSyncHealthStatus.RetiredOrigin;
                fault.NextRecoveryAttemptAtUtc = null;
                fault.BlocksPublishing = false; fault.BlocksMerge = false; fault.BlocksLogin = false;
                fault.BlocksLifecycle = false; fault.BlocksGarbageCollection = false;
                // RecoveredAtUtc/SupersedingRevision deliberately remain absent.
                faults.Update(fault);
            }
        }
        else
        {
            existing.Status = UserSyncSnapshotStatus.IsolatedFork;
            snapshots.Update(existing);
            await faultWriter.RecordAsync(new UserSyncFaultDescriptor
            {
                UserId = user.UId, Scope = UserSyncFaultScope.LocalCanonical,
                Kind = UserSyncFaultKind.CanonicalBundleLinkFailure, Status = UserSyncHealthStatus.TerminalConflict,
                OriginDeviceId = target.DeviceId, OriginInstanceId = target.OriginInstanceId, KeyEpoch = user.KeyEpoch,
                MembershipEpoch = user.MembershipEpoch, DiagnosticCode = "excluded-origin-canonical-not-proven-independent",
                BlocksPublishing = true, BlocksMerge = true, BlocksLogin = true, BlocksGarbageCollection = true, BlocksLifecycle = true
            }, ct);
        }
    }

    private async Task<bool> CanonicalExcludesOriginAsync(User user, UserMembershipAuthorization target, CancellationToken ct)
    {
        try
        {
            user.VerifyIntegrity();
            var checkpoint = await checkpoints.GetAsync(user.UId, ct);
            if (checkpoint is null) return false;
            UserCanonicalCheckpointUtil.Verify(user, checkpoint, identity);
            if ((await knowledge.ListForUserAsync(user.UId, ct)).Any(row => row.OriginDeviceId == target.DeviceId &&
                    row.OriginInstanceId == target.OriginInstanceId && row.HighestMergedRevision > 0)) return false;
            foreach (var row in await snapshots.ListForUserAsync(user.UId, ct))
            {
                if (row.Status is not (UserSyncSnapshotStatus.LocalPublished or UserSyncSnapshotStatus.MergedReceipt)) continue;
                var envelope = UserSnapshotEvidenceUtil.DeserializeExact(row);
                await membership.VerifySnapshotAuthorAsync(envelope, ct);
                if (envelope.OriginDeviceId == target.DeviceId || envelope.Coverage.Any(entry => entry.OriginDeviceId == target.DeviceId &&
                        entry.OriginInstanceId == target.OriginInstanceId && entry.OriginRevision > 0)) continue;
                if (MatchesCanonical(user, envelope)) return true;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or
                                       ArgumentException or System.Security.Cryptography.CryptographicException)
        { return false; }
        return false;
    }

    private static bool MatchesCanonical(User user, UserSnapshotEnvelope envelope)
    {
        var payload = envelope.User;
        return envelope.UserId == user.UId && envelope.UserKeyEpoch == user.KeyEpoch && envelope.MembershipEpoch == user.MembershipEpoch &&
            Hashing.Verify(payload.UsernameHash, user.UsernameHash) && Hashing.Verify(payload.UsernameSalt, user.UsernameSalt) &&
            Hashing.Verify(payload.PasswordSalt, user.PasswordSalt) && Hashing.Verify(payload.EncryptedPayload, user.EncryptedPayload) &&
            Hashing.Verify(payload.EncryptedGeneralUserDataPayload, user.EncryptedGeneralUserDataPayload) &&
            Hashing.Verify(payload.EncryptedUserPasswordsDataPayload, user.EncryptedUserPasswordsDataPayload) &&
            Hashing.Verify(payload.EncryptedUserDevicesDataPayload, user.EncryptedUserDevicesDataPayload) &&
            SyncVersionStampComparer.Instance.Equals(payload.GeneralUserDataVersion, user.GetGeneralUserDataVersion()) &&
            payload.UserDataLastModifiedAt == user.UserDataLastModifiedAt && payload.GeneralUserDataLastModifiedAt == user.GeneralUserDataLastModifiedAt &&
            payload.UserPasswordsDataLastModifiedAt == user.UserPasswordsDataLastModifiedAt && payload.UserDevicesDataLastModifiedAt == user.UserDevicesDataLastModifiedAt;
    }

    private async Task VerifyUncontestedHistoryAsync(User user, IReadOnlyList<UserMembershipAuthorization> members, CancellationToken ct)
    {
        var originKnowledge = await knowledge.ListForUserAsync(user.UId, ct);
        TombstoneGarbageCollectionRules.ValidateEvidenceStructure(user.UId, user.KeyEpoch, user.MembershipEpoch,
            members, await cutoffs.ListForUserAsync(user.UId, ct), originKnowledge);
        var history = await operations.ListForUserAsync(user.UId, ct);
        if (history.Any(row => row.Status != UserControlOperationStatus.Applied || row.OperationType is not
                (UserControlOperationType.DeviceAddition or UserControlOperationType.DeviceRemoval))) throw Blocked();
        if (history.LongCount() != user.MembershipEpoch - 1) throw Blocked();
        long epoch = 1;
        foreach (var row in history.OrderBy(row => row.PreviousMembershipEpoch))
        {
            var envelope = UserControlOperationEnvelopeUtil.Deserialize(row.EnvelopePayload);
            if (envelope.OperationId != row.OperationId || envelope.UserId != user.UId ||
                envelope.PreviousMembershipEpoch != epoch || envelope.ResultingMembershipEpoch != epoch + 1 ||
                !Hashing.Verify(envelope.OperationHash, row.OperationHash)) throw Blocked();
            await membership.VerifyControlAuthorAsync(envelope, ct);
            if (envelope.OperationType == UserControlOperationType.DeviceAddition)
            {
                var addition = UserControlOperationEnvelopeUtil.DeserializeDeviceAdditionPayload(envelope.OperationPayload);
                var member = members.SingleOrDefault(item => item.AuthorizationId == envelope.OperationId) ?? throw Blocked();
                if (addition.UserId != user.UId || addition.KeyEpoch != envelope.PreviousKeyEpoch ||
                    addition.PreviousMembershipEpoch != envelope.PreviousMembershipEpoch ||
                    addition.ResultingMembershipEpoch != envelope.ResultingMembershipEpoch ||
                    member.StartedMembershipEpoch != envelope.ResultingMembershipEpoch || member.MinimumKeyEpoch != addition.KeyEpoch ||
                    member.CreatedAtUtc.ToUniversalTime() != envelope.CreatedAtUtc.ToUniversalTime() ||
                    member.AdditionOperationId != envelope.OperationId || member.AdditionOperationHash is null ||
                    !Hashing.Verify(member.AdditionOperationHash, envelope.OperationHash) || member.DeviceId != addition.NewDeviceId ||
                    member.OriginInstanceId != addition.NewOriginInstanceId || !Hashing.Verify(member.SignPublicKey, addition.SignPublicKey) ||
                    !Hashing.Verify(member.AgreementPublicKeyHash, Hashing.SHA256Hash(addition.AgreementPublicKey)) ||
                    member.TlsCertFingerprint != SyncIdentityUtil.NormalizeFingerprint(addition.TlsCertFingerprint) || member.DeviceType != addition.DeviceType)
                    throw Blocked();
            }
            else
            {
                var removal = UserControlOperationEnvelopeUtil.DeserializeDeviceRemovalPayload(envelope.OperationPayload);
                if (removal.UserId != user.UId || removal.KeyEpoch != envelope.PreviousKeyEpoch ||
                    removal.PreviousMembershipEpoch != envelope.PreviousMembershipEpoch ||
                    removal.ResultingMembershipEpoch != envelope.ResultingMembershipEpoch) throw Blocked();
                foreach (var id in removal.Origins.Select(item => item.AuthorizationId).Distinct())
                {
                    var member = members.SingleOrDefault(item => item.AuthorizationId == id) ?? throw Blocked();
                    DeviceRemovalEvidenceUtil.ValidateAuthorization(removal, member);
                    if (member.RemovalOperationId != envelope.OperationId || member.RemovalOperationHash is null ||
                        !Hashing.Verify(member.RemovalOperationHash, envelope.OperationHash)) throw Blocked();
                }
            }
            epoch++;
        }
    }

    private static DeviceRemovalPayload CreateProofScope(User user, UserMembershipAuthorization target, DeviceRemovalForkProof proof) => new()
    {
        UserId = user.UId, RemovedDeviceId = target.DeviceId, KeyEpoch = user.KeyEpoch,
        PreviousMembershipEpoch = user.MembershipEpoch, ResultingMembershipEpoch = checked(user.MembershipEpoch + 1), ForkExclusion = proof,
        Origins = [new DeviceRemovalOriginCutoffPayload
        {
            AuthorizationId = target.AuthorizationId, OriginInstanceId = target.OriginInstanceId, UserKeyEpoch = 1,
            SignPublicKeyHash = target.SignPublicKeyHash.ToArray(), AdditionOperationId = target.AdditionOperationId,
            AdditionOperationHash = target.AdditionOperationHash?.ToArray()
        }]
    };

    private static InvalidOperationException Blocked() => new("Independent authority and canonical state could not be proven for this device removal.");
}
