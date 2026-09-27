using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Sync;

public sealed class UserDeviceSyncPayload
{
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public bool IsSyncOn { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public byte[] IntegrityHash { get; set; } = [];
}
