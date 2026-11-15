namespace PasswordManagerLocal.Common.Backend.Models;

public sealed record RevocationDeliverySnapshot(Guid OperationId, Guid UserId, RevocationDeliveryState State,
    int RequiredRecipients, int AppliedRecipients, int RetiredRecipients, bool HasUnexportedEvidence, string? Reason = null,
    RevocationDeliveryWorkKind WorkKind = RevocationDeliveryWorkKind.AwaitingRecipientEvidence)
{
    // Dormancy is never completion. Only an absent live operation in a verified, reset-blocked
    // historical context can be dormant. Any new live authority/receipt commit wakes reconciliation.
    public bool RequiresNetworking => State != RevocationDeliveryState.Completed &&
        WorkKind != RevocationDeliveryWorkKind.DormantHistoryResetEvidence;
}
