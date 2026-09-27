namespace PasswordManagerLocal.Common.Backend.Models;

public sealed class UserSyncSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid OriginDeviceId { get; set; }
    public Guid OriginInstanceId { get; set; }
    public long OriginRevision { get; set; }
    public long UserKeyEpoch { get; set; }
    public long MembershipEpoch { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public Guid? LastReceivedFromDeviceId { get; set; }
    public byte[] SnapshotHash { get; set; } = [];
    public byte[] OriginSignPublicKey { get; set; } = [];
    public byte[] OriginSignature { get; set; } = [];
    public byte[] EnvelopePayload { get; set; } = [];
    public UserSyncSnapshotStatus Status { get; set; } = UserSyncSnapshotStatus.Pending;
    public string? QuarantineReason { get; set; }
    public byte[]? ConflictingSnapshotHash { get; set; }
}
