namespace PasswordManagerLocal.Common.Backend.Sync;

public sealed class DeviceRemovalOriginCutoffPayload
{
    public Guid AuthorizationId { get; set; }
    public Guid OriginInstanceId { get; set; }
    public long UserKeyEpoch { get; set; }
    public long HighestAcceptedSnapshotRevision { get; set; }
    public long HighestAcceptedControlSequence { get; set; }
    public byte[] SignPublicKeyHash { get; set; } = [];
    public Guid? AdditionOperationId { get; set; }
    public byte[]? AdditionOperationHash { get; set; }
}
