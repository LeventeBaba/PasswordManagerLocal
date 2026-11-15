using System.Text.Json;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>Derives delivery obligations from the original signed historical declaration, never current links or transport ACKs.</summary>
public sealed class RevocationDeliveryService(IRevocationReceiptRepository evidence, IUserControlOperationRepository operations,
    IUserControlStateRepository states, IDeletedUserBarrierRepository barriers,
    IUserMembershipAuthorizationRepository membership) : IRevocationDeliveryService
{
    public async Task<bool> HasPendingDeliveryAsync(CancellationToken ct = default) =>
        (await GetSnapshotsAsync(ct)).Any(s => s.State != RevocationDeliveryState.Completed);

    public async Task<IReadOnlyList<Guid>> ListPendingUserIdsAsync(CancellationToken ct = default) =>
        (await GetSnapshotsAsync(ct)).Where(s => s.State != RevocationDeliveryState.Completed).Select(s => s.UserId).Distinct().Order().ToArray();

    public async Task<bool> HasPendingForPeerAsync(Guid deviceId, CancellationToken ct = default)
    {
        var pending = (await ListPendingUserIdsAsync(ct)).ToHashSet();
        return (await membership.ListUserIdsForDeviceAsync(deviceId, ct)).Any(pending.Contains);
    }

    public async Task StageHistoryResetAsync(Guid userId, CancellationToken ct = default)
    {
        var snapshots = (await GetSnapshotsAsync(ct)).ToDictionary(s => s.OperationId);
        foreach (var context in (await evidence.ListEvidenceAsync(ct)).Where(e => e.UserId == userId))
        {
            if (snapshots.TryGetValue(context.OperationId, out var state) && state.State == RevocationDeliveryState.Completed) continue;
            if (context.HistoryResetBlocked) continue;
            context.HistoryResetBlocked = true;
            evidence.UpdateEvidence(context);
        }
    }

    public async Task<IReadOnlyList<RevocationDeliverySnapshot>> GetSnapshotsAsync(CancellationToken ct = default)
    {
        var stored = await evidence.ListEvidenceAsync(ct);
        var live = (await operations.ListAllRelayableAsync(ct)).Where(o =>
            o.OperationType is UserControlOperationType.DeviceRemoval or UserControlOperationType.AccountDeletion).ToArray();
        var result = new Dictionary<Guid, RevocationDeliverySnapshot>();
        var contexts = new Dictionary<Guid, Context>();
        var conflicts = new Dictionary<Guid, bool>();
        foreach (var context in stored)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!conflicts.TryGetValue(context.UserId, out var conflicted))
                {
                    conflicted = (await states.GetAsync(context.UserId, ct))?.HasConflict == true ||
                        (await barriers.GetAsync(context.UserId, ct))?.HasConflict == true;
                    conflicts[context.UserId] = conflicted;
                }
                var row = await operations.GetByIdAsync(context.OperationId, ct);
                if (conflicted || context.HistoryResetBlocked || context.HasReceiptConflict ||
                    row is { Status: not UserControlOperationStatus.Applied })
                    throw new InvalidDataException("The revocation has unresolved authority, receipt or reset evidence.");
                var verified = RevocationReceiptService.ReadEvidence(context);
                var required = RevocationDeliveryHistoryUtil.Validate(context, verified.Operation, verified.Members);
                if (row is not null && (row.UserId != context.UserId || !Hashing.Verify(row.OperationHash, context.OperationHash) ||
                    row.OperationType != verified.Operation.OperationType || row.OriginDeviceId != verified.Operation.OriginDeviceId ||
                    row.OriginInstanceId != verified.Operation.OriginInstanceId || row.OriginSequence != verified.Operation.OriginSequence ||
                    row.PreviousMembershipEpoch != verified.Operation.PreviousMembershipEpoch || row.ResultingMembershipEpoch != verified.Operation.ResultingMembershipEpoch ||
                    row.PreviousKeyEpoch != verified.Operation.PreviousKeyEpoch || row.ResultingKeyEpoch != verified.Operation.ResultingKeyEpoch ||
                    row.ConflictingOperationHash is not null))
                    throw new InvalidDataException("The live revocation conflicts with its frozen identity.");
                var receipts = await evidence.ListAsync(context.OperationId, ct);
                foreach (var receipt in receipts)
                {
                    var signed = RevocationReceiptService.VerifyStored(receipt, verified.Operation, verified.Members);
                    if (signed.Kind == RevocationReceiptKind.AppliedReceiptStored)
                        RevocationReceiptService.ValidateCustody(signed, verified.Operation, receipts);
                }
                contexts.Add(context.OperationId, new(verified.Operation, required, receipts, await evidence.ListDebtsAsync(context.OperationId, ct)));
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or
                UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Security.Cryptography.CryptographicException or PasswordManagerLocal.Common.Backend.Exceptions.SyncStateUnavailableException)
            {
                result[context.OperationId] = Blocked(context.OperationId, context.UserId, "Unresolved revocation delivery evidence.");
            }
        }
        foreach (var operation in live)
            if (!contexts.ContainsKey(operation.OperationId) && !result.ContainsKey(operation.OperationId))
                result[operation.OperationId] = Blocked(operation.OperationId, operation.UserId,
                    "The revocation is pending application or lacks authenticated recipient evidence.");

        // All retirement dependencies advance membership, or end in account deletion. Evaluate
        // successors first, avoiding recursive completion or arrival-order dependence.
        foreach (var context in contexts.Values.OrderByDescending(c => c.Operation.PreviousMembershipEpoch)
                     .ThenByDescending(c => c.Operation.PreviousKeyEpoch).ThenBy(c => c.Operation.OperationId))
        {
            var operation = context.Operation;
            var retired = context.Required.Where(member => contexts.Values.Any(successor =>
                result.TryGetValue(successor.Operation.OperationId, out var successorState) &&
                successorState.State == RevocationDeliveryState.Completed && SafelyRetires(successor, context, member))).ToArray();
            var remaining = context.Required.Except(retired).ToArray();
            var applied = remaining.Select(member => context.Receipts.SingleOrDefault(r =>
                r.Kind == RevocationReceiptKind.Applied && r.RecipientAuthorizationId == member.AuthorizationId &&
                r.RecipientDeviceId == member.DeviceId && r.RecipientOriginInstanceId == member.OriginInstanceId)).ToArray();
            var appliedCount = applied.Count(r => r is not null);
            var custodyMissing = remaining.Length > 1 && applied.Where(r => r is not null).Any(receipt =>
                !context.Receipts.Any(custody => custody.Kind == RevocationReceiptKind.AppliedReceiptStored &&
                    custody.RecipientAuthorizationId != receipt!.RecipientAuthorizationId &&
                    remaining.Any(m => m.AuthorizationId == custody.RecipientAuthorizationId) &&
                    Hashing.Verify(custody.AttestedReceiptHash, receipt.ReceiptHash)));
            var evidenceDebt = context.Debts.Any(d => remaining.Any(m => m.AuthorizationId == d.PeerAuthorizationId &&
                m.DeviceId == d.PeerDeviceId && m.OriginInstanceId == d.PeerOriginInstanceId));
            var complete = appliedCount + retired.Length == context.Required.Length && !custodyMissing && !evidenceDebt;
            result[operation.OperationId] = new(operation.OperationId, operation.UserId,
                complete ? RevocationDeliveryState.Completed : appliedCount + retired.Length > 0
                    ? RevocationDeliveryState.PartiallyDelivered : RevocationDeliveryState.Pending,
                context.Required.Length, appliedCount, retired.Length, custodyMissing || evidenceDebt,
                complete ? null : evidenceDebt ? "A required peer's committed evidence inventory is still incomplete." : custodyMissing ? "Signed custody evidence remains outstanding." : "Required installations have not applied the revocation.");
        }
        return result.Values.OrderBy(s => s.UserId).ThenBy(s => s.OperationId).ToArray();
    }

    private static bool SafelyRetires(Context successor, Context original, UserMembershipAuthorization member)
    {
        var later = successor.Operation;
        if (later.UserId != original.Operation.UserId || later.PreviousMembershipEpoch < original.Operation.ResultingMembershipEpoch ||
            later.PreviousKeyEpoch < original.Operation.PreviousKeyEpoch || later.OperationId == original.Operation.OperationId) return false;
        if (!later.RevocationMembers.Any(m => m.AuthorizationId == member.AuthorizationId && m.DeviceId == member.DeviceId &&
                m.OriginInstanceId == member.OriginInstanceId && Hashing.Verify(m.SignPublicKeyHash, member.SignPublicKeyHash))) return false;
        if (later.OperationType == UserControlOperationType.AccountDeletion) return true;
        if (later.OperationType != UserControlOperationType.DeviceRemoval) return false;
        var removal = UserControlOperationEnvelopeUtil.DeserializeDeviceRemovalPayload(later.OperationPayload);
        return removal.RemovedDeviceId == member.DeviceId && removal.Origins.Any(o => o.AuthorizationId == member.AuthorizationId &&
            o.OriginInstanceId == member.OriginInstanceId && Hashing.Verify(o.SignPublicKeyHash, member.SignPublicKeyHash));
    }

    private static RevocationDeliverySnapshot Blocked(Guid operation, Guid user, string reason) =>
        new(operation, user, RevocationDeliveryState.Blocked, 0, 0, 0, false, reason);

    private sealed record Context(UserControlOperationEnvelope Operation, UserMembershipAuthorization[] Required,
        IReadOnlyList<StoredRevocationReceipt> Receipts, IReadOnlyList<RevocationEvidenceDebt> Debts);
}
