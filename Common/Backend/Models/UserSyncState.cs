namespace PasswordManagerLocal.Common.Backend.Models;

public sealed class UserSyncState
{
    public Guid UserId { get; set; }
    public Guid LocalOriginInstanceId { get; set; }
    public long NextOriginRevision { get; set; } = 1;
    public byte[] LastPublishedContentHash { get; set; } = [];
    public DateTimeOffset LastUpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
