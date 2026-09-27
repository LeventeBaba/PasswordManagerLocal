using System.Net;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Sync.Discovery.Protocol;

internal sealed class EnrollmentDiscoveryResponsePacket
{
    public long UnixTimeSeconds { get; init; }
    public byte[] QueryNonce { get; init; } = [];
    public string SessionId { get; init; } = string.Empty;
    public Guid DeviceId { get; init; }
    public Guid OriginInstanceId { get; init; }
    public DeviceType DeviceType { get; init; }
    public byte[] TlsFingerprint { get; init; } = [];
    public byte[] SignPublicKey { get; init; } = [];
    public byte[] AgreementPublicKey { get; init; } = [];
    public IPAddress ResponderAddress { get; init; } = IPAddress.None;
    public byte[] AuthenticatedBytes { get; init; } = [];
    public byte[] Mac { get; init; } = [];
}
