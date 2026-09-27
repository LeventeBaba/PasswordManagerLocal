namespace PasswordManagerLocal.Common.Backend.Models;

public sealed class SyncItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required Guid ModelId { get; set; }
    public required SyncModelType ModelType { get; set; }
    public required SyncChangeType ChangeType { get; set; }
    public long ChangedAtTs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public ICollection<SyncQueueItem> QueueItems { get; set; } = [];
}