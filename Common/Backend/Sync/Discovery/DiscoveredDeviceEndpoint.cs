namespace PasswordManagerLocal.Common.Backend.Sync.Discovery;

public sealed class DiscoveredDeviceEndpoint
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string TlsCertFingerprint { get; init; }
    // Only relayed hints expire; directly observed endpoints retain existing registry semantics.
    public DateTimeOffset? ExpiresAtUtc { get; init; }
}
