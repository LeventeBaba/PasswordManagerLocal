namespace PasswordManagerLocal.Common.Backend.Sync;

public sealed class DeviceRemovalPayload
{
    public Guid UserId { get; set; }
    public Guid RemovedDeviceId { get; set; }
    public long PreviousMembershipEpoch { get; set; }
    public long ResultingMembershipEpoch { get; set; }
    public long KeyEpoch { get; set; }
    public List<DeviceRemovalOriginCutoffPayload> Origins { get; set; } = [];
    public byte[] IntegrityHash { get; set; } = [];
}
