using System.Text;
using System.Text.Json;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Sync;

/// <summary>
/// Recovered ordinary origin faults keep their immutable identity, bad hashes, category,
/// revision, affected component and recovery identity. Only free-form diagnostic text is redundant.
/// </summary>
internal static class RecoveredOriginFaultCompactionPolicy
{
    internal static async Task<StorageEvidenceRetentionDecision> AnalyzeAsync(SqliteSession session,
        UserSyncFault fault, IUserMembershipAuthorizationService? membership, CancellationToken ct)
    {
        StorageEvidenceRetentionDecision Keep(StorageRetentionReason reason) => new("UserSyncFaults", fault.Id, fault.UserId, reason);
        if (fault.Status == UserSyncHealthStatus.TerminalConflict || fault.Scope is
            UserSyncFaultScope.SnapshotFork or UserSyncFaultScope.ControlPlane or UserSyncFaultScope.DeterministicItem)
            return Keep(StorageRetentionReason.RequiredForForkDetection);
        if (fault.Status != UserSyncHealthStatus.Superseded || fault.Scope != UserSyncFaultScope.SnapshotOrigin ||
            fault.Kind is not (UserSyncFaultKind.IncomingDecryptFailure or UserSyncFaultKind.IncomingIntegrityFailure or
                UserSyncFaultKind.IncomingMetadataMismatch) || fault.BlocksPublishing || fault.BlocksMerge || fault.BlocksLogin ||
            fault.BlocksGarbageCollection || fault.BlocksLifecycle || fault.RecoveredAtUtc is null ||
            fault.ConflictingHash.Length != 0)
            return Keep(StorageRetentionReason.RequiredByRecovery);
        if (fault.Id == Guid.Empty || fault.UserId == Guid.Empty ||
            fault.OriginDeviceId is not Guid device || device == Guid.Empty ||
            fault.OriginInstanceId is not Guid instance || instance == Guid.Empty ||
            fault.KeyEpoch is not long epoch || epoch <= 0 || fault.MembershipEpoch is not > 0 ||
            fault.OriginRevision is not long badRevision || badRevision <= 0 ||
            fault.SupersedingRevision is not long replacement || replacement <= badRevision ||
            fault.ObservedHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
            return Keep(StorageRetentionReason.InsufficientProof);
        if (fault.DiagnosticCode.Length == 0)
            return Keep(StorageRetentionReason.AlreadyCompact);
        if (membership is null) return Keep(StorageRetentionReason.InsufficientProof);

        if (await session.ExistsAsync("""
            SELECT 1 FROM "DeletedUserBarriers" WHERE "UserId" = $user
            UNION ALL SELECT 1 FROM "CriticalControlIngress" WHERE "UserId" = $user
            """, ct, ("$user", fault.UserId))) return Keep(StorageRetentionReason.RequiredByRevocation);
        if (await session.ExistsAsync("""
            SELECT 1 FROM "UserRecoveryJournals" WHERE "UserId" = $user
            """, ct, ("$user", fault.UserId))) return Keep(StorageRetentionReason.RequiredByRecovery);
        if (await session.ExistsAsync("""
            SELECT 1 FROM "UserControlStates" WHERE "UserId" = $user AND "HasConflict" = 1
            UNION ALL SELECT 1 FROM "UserSyncFaults" WHERE "UserId" = $user
                AND "Status" NOT IN ($recovered, $superseded)
                AND ("Status" = $terminal OR "BlocksGarbageCollection" = 1 OR "BlocksLifecycle" = 1
                     OR "BlocksLogin" = 1 OR "BlocksMerge" = 1 OR "BlocksPublishing" = 1)
            """, ct, ("$user", fault.UserId), ("$recovered", (byte)UserSyncHealthStatus.Recovered),
            ("$superseded", (byte)UserSyncHealthStatus.Superseded), ("$terminal", (byte)UserSyncHealthStatus.TerminalConflict)))
            return Keep(StorageRetentionReason.BlockedByConflict);
        if (!await session.ExistsAsync("""
            SELECT 1 FROM "Users" WHERE "UId" = $user AND "KeyEpoch" = $epoch AND "MembershipEpoch" >= $membership
            """, ct, ("$user", fault.UserId), ("$epoch", epoch), ("$membership", fault.MembershipEpoch)))
            return Keep(StorageRetentionReason.RequiredByRecovery);

        try
        {
            var snapshot = await new UserSyncSnapshotRepository(session).GetAsync(fault.UserId, device, instance, epoch, ct);
            if (snapshot is null || snapshot.OriginRevision != replacement || snapshot.Status is not
                (UserSyncSnapshotStatus.MergedReceipt or UserSyncSnapshotStatus.LocalPublished))
                return Keep(StorageRetentionReason.RequiredByRecovery);
            var known = await new UserRevisionKnowledgeRepository(session).GetAsync(fault.UserId, device, instance, epoch, ct);
            if (known is null || known.HighestStoredRevision != replacement || known.HighestMergedRevision < replacement ||
                !Hashing.Verify(known.HighestStoredSnapshotHash, snapshot.SnapshotHash))
                return Keep(StorageRetentionReason.InsufficientProof);
            var envelope = UserSnapshotEvidenceUtil.DeserializeExact(snapshot);
            await membership.VerifySnapshotAuthorAsync(envelope, ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or JsonException or
                                   ArgumentException or FormatException or OverflowException or InvalidCastException or System.Security.Cryptography.CryptographicException)
        {
            return Keep(StorageRetentionReason.InsufficientProof);
        }
        return new("UserSyncFaults", fault.Id, fault.UserId, StorageRetentionReason.SafeToCompact,
            Encoding.UTF8.GetByteCount(fault.DiagnosticCode));
    }

    internal static bool SameCapture(UserSyncFault left, UserSyncFault right) =>
        left.Id == right.Id && left.UserId == right.UserId && left.Scope == right.Scope && left.Kind == right.Kind &&
        left.Status == right.Status && left.AffectedComponent == right.AffectedComponent && left.DiagnosticCode == right.DiagnosticCode &&
        left.OriginDeviceId == right.OriginDeviceId && left.OriginInstanceId == right.OriginInstanceId &&
        left.KeyEpoch == right.KeyEpoch && left.MembershipEpoch == right.MembershipEpoch && left.OriginRevision == right.OriginRevision &&
        left.ExpectedHash.AsSpan().SequenceEqual(right.ExpectedHash) && left.ObservedHash.AsSpan().SequenceEqual(right.ObservedHash) &&
        left.ConflictingHash.AsSpan().SequenceEqual(right.ConflictingHash) && left.FirstDetectedAtUtc == right.FirstDetectedAtUtc &&
        left.LastDetectedAtUtc == right.LastDetectedAtUtc && left.LastRecoveryAttemptAtUtc == right.LastRecoveryAttemptAtUtc &&
        left.RecoveredAtUtc == right.RecoveredAtUtc && left.NextRecoveryAttemptAtUtc == right.NextRecoveryAttemptAtUtc &&
        left.SupersedingRevision == right.SupersedingRevision && left.RecoveryAttemptCount == right.RecoveryAttemptCount &&
        left.BlocksPublishing == right.BlocksPublishing && left.BlocksMerge == right.BlocksMerge && left.BlocksLogin == right.BlocksLogin &&
        left.BlocksGarbageCollection == right.BlocksGarbageCollection && left.BlocksLifecycle == right.BlocksLifecycle;
}
