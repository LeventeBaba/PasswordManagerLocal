namespace PasswordManagerLocal.Common.Backend.Models;

public sealed record RevocationDeliverySnapshot(Guid OperationId, Guid UserId, RevocationDeliveryState State,
    int RequiredRecipients, int AppliedRecipients, int RetiredRecipients, bool HasUnexportedEvidence, string? Reason = null);
