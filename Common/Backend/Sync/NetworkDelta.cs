namespace PasswordManagerLocal.Common.Backend.Sync;

public sealed class NetworkDelta
{
    public string Entity { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = [];
    public long Ts { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public byte[] SignPub { get; set; } = [];
    public byte[] Sig { get; set; } = [];
    public string RecipientDeviceId { get; set; } = string.Empty;
    public int EncryptionVersion { get; set; }
    public byte[] EphemeralPublicKey { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] Tag { get; set; } = [];
    public byte[] PayloadHash { get; set; } = [];

    // Sender-local metadata used to match explicit snapshot acknowledgements.
    // These fields are derived from the immutable inner envelope and are not serialized on the wire.
    public Guid SnapshotUserId { get; set; }
    public Guid SnapshotOriginDeviceId { get; set; }
    public Guid SnapshotOriginInstanceId { get; set; }
    public long SnapshotOriginRevision { get; set; }
    public byte[] SnapshotHash { get; set; } = [];

    // Sender-local metadata used to match exact durable control-operation receipts.
    public Guid ControlOperationId { get; set; }
    public Guid ControlOperationUserId { get; set; }
    public Guid ControlOperationOriginDeviceId { get; set; }
    public Guid ControlOperationOriginInstanceId { get; set; }
    public long ControlOperationOriginSequence { get; set; }
    public byte[] ControlOperationHash { get; set; } = [];
}
