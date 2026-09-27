namespace PasswordManagerLocal.Common.Backend.Models;

public sealed class UserRevisionKnowledge
{
    public Guid UserId { get; set; }
    public Guid OriginDeviceId { get; set; }
    public Guid OriginInstanceId { get; set; }
    public long UserKeyEpoch { get; set; }
    public long HighestStoredRevision { get; set; }
    public byte[] HighestStoredSnapshotHash { get; set; } = [];
    public long HighestMergedRevision { get; set; }
    public DateTimeOffset LastUpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
