namespace PasswordManagerLocal.Common.Backend.Models;

public sealed class StoredRevocationReceipt
{
    public Guid OperationId { get; set; }
    public Guid RecipientDeviceId { get; set; }
    public Guid RecipientOriginInstanceId { get; set; }
    public Guid RecipientAuthorizationId { get; set; }
    public RevocationReceiptKind Kind { get; set; }
    public byte[] AttestedReceiptHash { get; set; } = [];
    public Guid UserId { get; set; }
    public byte[] ReceiptHash { get; set; } = [];
    public byte[] Payload { get; set; } = [];
}
