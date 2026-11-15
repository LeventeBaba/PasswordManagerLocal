namespace PasswordManagerLocal.Common.Backend.Sync;

public sealed class DeviceRemovalOriginCutoffPayload
{
    private byte[]? _additionOperationHash;

    public Guid AuthorizationId { get; set; }
    public Guid OriginInstanceId { get; set; }
    public long UserKeyEpoch { get; set; }
    public long HighestAcceptedSnapshotRevision { get; set; }
    public long HighestAcceptedControlSequence { get; set; }
    public byte[] SignPublicKeyHash { get; set; } = [];
    public Guid? AdditionOperationId { get; set; }
    // A genesis installation has no addition hash; empty wire/storage values are absent.
    public byte[]? AdditionOperationHash
    {
        get => _additionOperationHash;
        set => _additionOperationHash = value is { Length: 0 } ? null : value;
    }
}
