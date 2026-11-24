namespace PasswordManagerLocal.Common.Backend.Models;

public enum RevocationDeliveryWorkKind
{
    AwaitingRecipientEvidence,
    PendingEvidenceCustody,
    DormantHistoryResetEvidence,
    BlockedHistoryResetEvidence,
    AuthorityConflict,
    UnverifiedEvidence,
    Completed
}
