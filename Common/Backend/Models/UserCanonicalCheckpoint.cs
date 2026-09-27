namespace PasswordManagerLocal.Common.Backend.Models;

/// <summary>
/// Independently signed commitment to the synchronized canonical User row. SavedKey is local-only
/// and is intentionally excluded from the committed content.
/// </summary>
public sealed class UserCanonicalCheckpoint
{
    public Guid UserId { get; set; }
    public long CheckpointSequence { get; set; }
    public Guid LocalDeviceId { get; set; }
    public Guid LocalOriginInstanceId { get; set; }
    public long KeyEpoch { get; set; }
    public long MembershipEpoch { get; set; }
    public byte[] CanonicalContentHash { get; set; } = [];
    public byte[] UserIntegrityHash { get; set; } = [];
    public byte[] SignPublicKey { get; set; } = [];
    public byte[] Signature { get; set; } = [];
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
