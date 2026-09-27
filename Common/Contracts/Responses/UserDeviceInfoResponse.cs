using PasswordManagerLocal.Common.Contracts.Devices;

namespace PasswordManagerLocal.Common.Contracts.Responses;

public sealed class UserDeviceInfoResponse
{
    public Guid DeviceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DeviceType DeviceType { get; set; }
    public string TlsCertFingerprint { get; set; } = string.Empty;
    public DateTime? LastSync { get; set; }
    public DateTime? LastSeen { get; set; }
    public DateTime? LastLoginDate { get; set; }
    public DateTime? PreviousLoginDate { get; set; }
    public bool IsTrusted { get; set; }
    public bool IsBlocked { get; set; }
    public string? BlockedReason { get; set; }
    public DateTimeOffset? BlockedAt { get; set; }
    public int InvalidSyncAttemptCount { get; set; }
    public bool IsSyncOn { get; set; }
    public bool IsOnline { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public bool IsCurrentDevice { get; set; }
}
