using PasswordManagerLocal.Common.Backend.Constants;
using System.Net;
using System.Security.Cryptography;
using System.Text;

using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Sync.Enrollment;

public sealed class DeviceEnrollmentDirectEndpointInfo
{
    public Guid DeviceId { get; set; }
    public Guid OriginInstanceId { get; set; }
    public string TlsCertFingerprint { get; set; } = string.Empty;
    public byte[] SignPublicKey { get; set; } = [];
    public byte[] AgreementPublicKey { get; set; } = [];
    public DeviceType DeviceType { get; set; }
    public int Port { get; set; }
    public IReadOnlyList<string> Hosts { get; set; } = [];
}
