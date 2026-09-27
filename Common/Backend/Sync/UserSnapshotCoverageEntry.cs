namespace PasswordManagerLocal.Common.Backend.Sync;

public sealed class UserSnapshotCoverageEntry
{
    public Guid OriginDeviceId { get; set; }
    public Guid OriginInstanceId { get; set; }
    public long UserKeyEpoch { get; set; }
    public long OriginRevision { get; set; }
}
