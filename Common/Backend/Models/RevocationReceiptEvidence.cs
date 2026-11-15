namespace PasswordManagerLocal.Common.Backend.Models;

/// <summary>Frozen, locally verified public evidence; never an authority source for accepting new data.</summary>
public sealed class RevocationReceiptEvidence
{
    public Guid OperationId { get; set; }
    public Guid UserId { get; set; }
    public byte[] OperationHash { get; set; } = [];
    public byte[] EnvelopePayload { get; set; } = [];
    public byte[] AuthorizationPayload { get; set; } = [];
    public byte[] MembershipProofPayload { get; set; } = [];
    public bool HistoryResetBlocked { get; set; }
    public bool HasReceiptConflict { get; set; }
}
