namespace PasswordManagerLocal.Common.Backend.Sync;

public sealed class UserSnapshotEnvelope
{
    public Guid UserId { get; set; }
    public Guid OriginDeviceId { get; set; }
    public Guid OriginInstanceId { get; set; }
    public long OriginRevision { get; set; }
    public long UserKeyEpoch { get; set; }
    public long MembershipEpoch { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public UserSyncPayload User { get; set; } = new();
    public List<UserSnapshotCoverageEntry> Coverage { get; set; } = [];
    public byte[] SnapshotHash { get; set; } = [];
    public byte[] OriginSignPublicKey { get; set; } = [];
    public byte[] OriginSignature { get; set; } = [];
}
