using PasswordManagerLocal.Common.Backend.Constants;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace PasswordManagerLocal.Common.Backend.Sync.Enrollment;

public sealed class DeviceEnrollmentParsedCode
{
    public string SessionId { get; set; } = string.Empty;
    public byte[] Secret { get; set; } = [];
    public List<DeviceEnrollmentParsedDirectEndpoint> DirectEndpoints { get; set; } = [];
}
