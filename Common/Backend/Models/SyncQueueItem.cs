namespace PasswordManagerLocal.Common.Backend.Models;

public sealed class SyncQueueItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public long QueueId { get; set; }

    public DateTimeOffset EnqueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }

    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }

    public Guid SyncItemId { get; set; }
    public SyncItem? SyncItem { get; set; }
}
