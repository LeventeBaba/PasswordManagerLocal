using System.Text.Json;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Sync;

/// <summary>
/// Removes only duplicate auxiliary proof copies. Original signed operations, recipient
/// declarations, authorization contexts and the complete independent proof column survive.
/// No receipt, debt, cutoff, historical operation or deletion barrier is deleted.
/// </summary>
internal static class RevocationEvidenceCompactionPolicy
{
    internal static (StorageRetentionReason Reason, byte[]? Envelope) Analyze(RevocationReceiptEvidence evidence)
    {
        if (evidence.HistoryResetBlocked || evidence.HasReceiptConflict)
            return (StorageRetentionReason.BlockedByConflict, null);
        if (evidence.EnvelopePayload.Length == 0 ||
            evidence.EnvelopePayload.Length > SyncConstants.MaxUserControlOperationEnvelopeBytes ||
            evidence.MembershipProofPayload.Length == 0 ||
            evidence.MembershipProofPayload.Length > SyncConstants.MaxIncomingDeltaTotalBytesPerCall)
            return (StorageRetentionReason.InsufficientProof, null);

        try
        {
            var (operation, members) = RevocationReceiptService.ReadEvidence(evidence);
            if (operation.OperationType is not (UserControlOperationType.DeviceRemoval or UserControlOperationType.AccountDeletion))
                return (StorageRetentionReason.InsufficientProof, null);
            if (operation.RevocationHistoryProofs.Count == 0)
                return (StorageRetentionReason.AlreadyCompact, null);

            // This authenticates the complete predecessor chain against the signed recipient
            // declaration, including removals and their exact installation cutoffs.
            RevocationDeliveryHistoryUtil.Validate(evidence, operation, members);
            var retained = JsonSerializer.Deserialize(evidence.MembershipProofPayload,
                BackendJsonSerializerContext.Default.RevocationHistoryProofEnvelopes)!;
            var exactProofs = retained.ToDictionary(proof => proof.OperationId, UserControlOperationEnvelopeUtil.Serialize);
            foreach (var duplicate in operation.RevocationHistoryProofs)
            {
                var exact = UserControlOperationEnvelopeUtil.Serialize(duplicate);
                if (!exactProofs.TryGetValue(duplicate.OperationId, out var replacement) || !exact.AsSpan().SequenceEqual(replacement))
                    return (StorageRetentionReason.InsufficientProof, null);
            }

            // Auxiliary lists are outside the signed core in protocol 16. CopyCoreProof
            // preserves the exact payload, hash, signature and signed recipient declarations.
            var compact = UserControlOperationEnvelopeUtil.Serialize(UserControlOperationEnvelopeUtil.CopyCoreProof(operation));
            return compact.Length < evidence.EnvelopePayload.Length
                ? (StorageRetentionReason.SafeToCompact, compact)
                : (StorageRetentionReason.AlreadyCompact, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or
                                   InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            return (StorageRetentionReason.InsufficientProof, null);
        }
    }

    internal static StorageEvidenceRetentionDecision Describe(RevocationReceiptEvidence evidence)
    {
        var (reason, compact) = Analyze(evidence);
        return new("RevocationReceiptEvidence", evidence.OperationId, evidence.UserId, reason,
            compact is null ? 0 : evidence.EnvelopePayload.LongLength - compact.LongLength);
    }
}
