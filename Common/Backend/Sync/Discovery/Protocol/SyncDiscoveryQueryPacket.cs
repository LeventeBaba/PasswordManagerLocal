namespace PasswordManagerLocal.Common.Backend.Sync.Discovery.Protocol;

internal sealed class SyncDiscoveryQueryPacket
{
    public long UnixTimeSeconds { get; init; }
    public byte[] Nonce { get; init; } = [];
    public Guid RequesterDeviceId { get; init; }
    public byte[] AuthenticatedBytes { get; init; } = [];
    public byte[] Signature { get; init; } = [];
}
