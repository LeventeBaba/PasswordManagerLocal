using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Sync.Enrollment;

public sealed class DeviceEnrollmentGroupSnapshot
{
    public Guid Id { get; set; }
    public byte[] EncryptedPayload { get; set; } = [];
    public DateTimeOffset LastModifiedAt { get; set; }
    public byte[] IntegrityHash { get; set; } = [];
    public List<Guid> UserIds { get; set; } = [];
}
