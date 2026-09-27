using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Sync;

public sealed class SyncDeltaPayload
{
    public Guid ModelId { get; set; }
    public SyncModelType ModelType { get; set; }
    public SyncChangeType ChangeType { get; set; }
    public UserSnapshotEnvelope? UserSnapshot { get; set; }
    public UserControlOperationEnvelope? UserControlOperation { get; set; }
    public GroupSyncPayload? Group { get; set; }
    public DeviceSyncPayload? Device { get; set; }
    public UserDeviceSyncPayload? UserDevice { get; set; }
}

