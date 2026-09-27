namespace PasswordManagerLocal.Common.Backend.Sync.Discovery.Protocol;

internal sealed class EnrollmentDiscoveryQueryPacket
{
    public long UnixTimeSeconds { get; init; }
    public byte[] Nonce { get; init; } = [];
    public string SessionId { get; init; } = string.Empty;
    public byte[] AuthenticatedBytes { get; init; } = [];
    public byte[] Mac { get; init; } = [];
}
