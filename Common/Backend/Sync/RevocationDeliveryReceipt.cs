using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Sync;

/// <summary>Original installation evidence; the forwarding peer is never the acknowledging identity.</summary>
public sealed class RevocationDeliveryReceipt
{
    public int Version { get; set; } = 2;
    public Guid UserId { get; set; }
    public Guid OperationId { get; set; }
    public byte[] OperationHash { get; set; } = [];
    public UserControlOperationType OperationType { get; set; }
    public long KeyEpoch { get; set; }
    public long MembershipEpoch { get; set; }
    public Guid RecipientDeviceId { get; set; }
    public Guid RecipientOriginInstanceId { get; set; }
    public Guid RecipientAuthorizationId { get; set; }
    public RevocationReceiptKind Kind { get; set; }
    public byte[] AttestedReceiptHash { get; set; } = [];
    public byte[] SignPublicKey { get; set; } = [];
    public byte[] Signature { get; set; } = [];
}
