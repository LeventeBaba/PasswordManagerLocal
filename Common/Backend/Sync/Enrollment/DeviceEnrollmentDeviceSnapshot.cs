using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Sync.Enrollment;

public sealed class DeviceEnrollmentDeviceSnapshot
{
    public Guid Id { get; set; }
    public byte[] PublicKey { get; set; } = [];
    public byte[] SignPublicKey { get; set; } = [];
    public string TlsCertFingerprint { get; set; } = string.Empty;
    public DeviceType DeviceType { get; set; }
    public byte[] LastKnownHash { get; set; } = [];
    public DateTime LastSync { get; set; }
    public DateTime LastSeen { get; set; }
    public bool IsTrusted { get; set; }
    public bool IsBlocked { get; set; }
    public string? BlockedReason { get; set; }
    public DateTimeOffset? BlockedAt { get; set; }
    public int InvalidSyncAttemptCount { get; set; }
    public DateTimeOffset? LastInvalidSyncAttemptAt { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; }
    public byte[] IntegrityHash { get; set; } = [];
    public List<Guid> UserIds { get; set; } = [];
}
