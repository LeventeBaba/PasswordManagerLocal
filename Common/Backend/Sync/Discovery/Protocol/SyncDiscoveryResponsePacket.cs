using System.Net;

namespace PasswordManagerLocal.Common.Backend.Sync.Discovery.Protocol;

internal sealed class SyncDiscoveryResponsePacket
{
    public long UnixTimeSeconds { get; init; }
    public byte[] QueryNonce { get; init; } = [];
    public Guid RequesterDeviceId { get; init; }
    public Guid ResponderDeviceId { get; init; }
    public byte[] TlsFingerprint { get; init; } = [];
    public IPAddress ResponderAddress { get; init; } = IPAddress.None;
    public byte[] AuthenticatedBytes { get; init; } = [];
    public byte[] Signature { get; init; } = [];
}
