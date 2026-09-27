using PasswordManagerLocal.Common.Backend.Sync.Discovery;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;

public interface IDiscoveredDeviceEndpointRegistry
{
    void AddOrUpdate(DiscoveredDeviceEndpoint endpoint);
    bool TryGetByFingerprint(string tlsFingerprint, out DiscoveredDeviceEndpoint? endpoint);
    bool IsRecentlyDiscovered(string tlsFingerprint, TimeSpan maximumAge);
    bool TryRemove(string tlsFingerprint);
    void Clear();
}
