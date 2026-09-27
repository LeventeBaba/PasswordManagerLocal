namespace PasswordManagerLocal.Common.Backend.Models;

public sealed class SyncTombstone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid ModelId { get; set; }
    public required SyncModelType ModelType { get; set; }
    public required long DeletedAtTs { get; set; }
}
