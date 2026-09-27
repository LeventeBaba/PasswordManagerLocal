using System.Net;

namespace PasswordManagerLocal.Common.Backend.Sync.Discovery;

internal sealed class LocalNetworkAddressCandidate
{
    public IPAddress Address { get; init; } = IPAddress.None;
    public int Priority { get; init; }
    public bool IsVirtualAdapter { get; init; }
}
