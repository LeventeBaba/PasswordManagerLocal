using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Sync.Enrollment;

public sealed class EnrollmentEndpoint
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public Guid DeviceId { get; set; }
    public Guid OriginInstanceId { get; set; }
    public string TlsCertFingerprint { get; set; } = string.Empty;
    public byte[] SignPublicKey { get; set; } = [];
    public byte[] AgreementPublicKey { get; set; } = [];
    public DeviceType DeviceType { get; set; }
}
