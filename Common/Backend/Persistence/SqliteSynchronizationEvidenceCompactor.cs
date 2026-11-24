using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Contracts.Errors;

namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>Bounded capture followed by exact revalidation under lifecycle ownership and a write transaction.</summary>
internal sealed class SqliteSynchronizationEvidenceCompactor(SqliteSession session, IUserLifecycleCoordinator lifecycle,
    IUserMembershipAuthorizationService? membershipAuthorization = null)
{
    public async Task<long> CompactAsync(Guid? userId = null, CancellationToken ct = default)
    {
        var revocations = await CompactRevocationsAsync(userId, null, ct);
        var faults = await CompactFaultsAsync(userId, null, ct);
        return revocations.ReclaimedBytes + faults.ReclaimedBytes;
    }

    public Task<EvidenceCompactionBatch> CompactBatchAsync(EvidenceCompactionCursor? cursor = null,
        Guid? userId = null, CancellationToken ct = default) => cursor?.Category switch
    {
        null or "Revocation" => CompactRevocationsAsync(userId, cursor?.AfterKey, ct),
        "Faults" => CompactFaultsAsync(userId, cursor.AfterKey, ct),
        _ => throw new ArgumentException("Unknown evidence compaction cursor.", nameof(cursor))
    };

    private void RequireCleanScope()
    {
        if (session.HasActiveTransaction || session.HasPendingChanges)
            throw new InvalidOperationException("Evidence maintenance requires a clean unit-of-work scope.");
    }

    private async Task<EvidenceCompactionBatch> CompactRevocationsAsync(Guid? userId, string? afterKey, CancellationToken ct)
    {
        RequireCleanScope();
        var repository = new RevocationReceiptRepository(session);
        var captured = await session.ReadSnapshotAsync(() => repository.ReadCompactionPageAsync(userId, afterKey, ct), ct);
        long reclaimed = 0;
        foreach (var candidate in captured.Candidates)
        {
            if (RevocationEvidenceCompactionPolicy.Analyze(candidate).Reason != StorageRetentionReason.SafeToCompact)
                continue;
            reclaimed += await UnderLifecycleAsync(candidate.UserId, async token =>
            {
                await using var transaction = await session.BeginTransactionAsync(token);
                try
                {
                    var current = await repository.GetEvidenceAsync(candidate.OperationId, token);
                    if (current is null || !SameCapture(candidate, current))
                        return 0L;
                    var (reason, compact) = RevocationEvidenceCompactionPolicy.Analyze(current);
                    if (reason != StorageRetentionReason.SafeToCompact || compact is null) return 0L;
                    var released = current.EnvelopePayload.LongLength - compact.LongLength;
                    repository.StageCompactedEnvelope(current, compact);
                    await session.SaveChangesAsync(token);
                    await transaction.CommitAsync(token);
                    return released;
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    session.ClearTrackedChanges();
                    throw;
                }
            }, ct);
        }
        return new(reclaimed, captured.NextKey is null ? new("Faults") : new("Revocation", captured.NextKey));
    }

    private async Task<EvidenceCompactionBatch> CompactFaultsAsync(Guid? userId, string? afterKey, CancellationToken ct)
    {
        RequireCleanScope();
        var repository = new UserSyncFaultRepository(session);
        var captured = await session.ReadSnapshotAsync(() => repository.ReadCompactionPageAsync(userId, afterKey, ct), ct);
        long reclaimed = 0;
        foreach (var candidate in captured.Candidates)
        {
            var analysis = await session.ReadSnapshotAsync(() => RecoveredOriginFaultCompactionPolicy.AnalyzeAsync(
                session, candidate, membershipAuthorization, ct), ct);
            if (analysis.Reason != StorageRetentionReason.SafeToCompact) continue;
            reclaimed += await UnderLifecycleAsync(candidate.UserId, async token =>
            {
                await using var transaction = await session.BeginTransactionAsync(token);
                try
                {
                    var current = await repository.GetByIdAsync(candidate.Id, token);
                    if (current is null || !RecoveredOriginFaultCompactionPolicy.SameCapture(candidate, current)) return 0L;
                    var checkedDecision = await RecoveredOriginFaultCompactionPolicy.AnalyzeAsync(session, current, membershipAuthorization, token);
                    if (checkedDecision.Reason != StorageRetentionReason.SafeToCompact) return 0L;
                    repository.StageCompactedDiagnostics(current);
                    await session.SaveChangesAsync(token);
                    await transaction.CommitAsync(token);
                    return checkedDecision.ReclaimableBytes;
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    session.ClearTrackedChanges();
                    throw;
                }
            }, ct);
        }
        return new(reclaimed, captured.NextKey is null ? null : new("Faults", captured.NextKey));
    }

    private async Task<long> UnderLifecycleAsync(Guid userId, Func<CancellationToken, Task<long>> action, CancellationToken ct)
    {
        try { return await lifecycle.ExecuteAsync(userId, action, ct); }
        catch (PendingSecurityOperationException)
        {
            // A profile-specific ingress fence retains this record while the read cursor
            // can still progress to unrelated profiles. Never bypass the fence for cleanup.
            ct.ThrowIfCancellationRequested();
            return 0;
        }
    }

    private static bool SameCapture(RevocationReceiptEvidence captured, RevocationReceiptEvidence current) =>
        captured.OperationId == current.OperationId && captured.UserId == current.UserId &&
        captured.HistoryResetBlocked == current.HistoryResetBlocked && captured.HasReceiptConflict == current.HasReceiptConflict &&
        captured.OperationHash.AsSpan().SequenceEqual(current.OperationHash) &&
        captured.EnvelopePayload.AsSpan().SequenceEqual(current.EnvelopePayload) &&
        captured.AuthorizationPayload.AsSpan().SequenceEqual(current.AuthorizationPayload) &&
        captured.MembershipProofPayload.AsSpan().SequenceEqual(current.MembershipProofPayload);
}
