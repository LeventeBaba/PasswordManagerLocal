namespace PasswordManagerLocal.Common.Backend.Models;

/// <summary>Known missing signed evidence at an exact required installation; cleared by its authenticated committed inventory.</summary>
public sealed class RevocationEvidenceDebt
{
    public Guid OperationId { get; set; }
    public Guid PeerDeviceId { get; set; }
    public Guid PeerOriginInstanceId { get; set; }
    public Guid PeerAuthorizationId { get; set; }
    public byte[] ReceiptHash { get; set; } = [];
}
