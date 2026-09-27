using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class RecordingDiscoveredDeviceEndpointRegistry : IDiscoveredDeviceEndpointRegistry
{
    public TimeSpan? LastMaximumAge { get; private set; }
    public bool RecentlyDiscoveredResult { get; set; }

    public void AddOrUpdate(DiscoveredDeviceEndpoint endpoint)
    {
    }

    public bool TryGetByFingerprint(string tlsFingerprint, out DiscoveredDeviceEndpoint? endpoint)
    {
        endpoint = null;
        return false;
    }

    public bool IsRecentlyDiscovered(string tlsFingerprint, TimeSpan maximumAge)
    {
        LastMaximumAge = maximumAge;
        return RecentlyDiscoveredResult;
    }

    public bool TryRemove(string tlsFingerprint) => false;

    public void Clear()
    {
    }
}
