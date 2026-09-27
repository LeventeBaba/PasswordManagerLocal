using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Sync.Presence;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;

public interface IDevicePresenceProbeService
{
    Task<DevicePresenceProbeResult> ProbeAsync(
        Device device,
        bool force = false,
        CancellationToken cancellationToken = default);

    void OnEndpointDiscovered(
        Device device,
        DiscoveredDeviceEndpoint endpoint);
}
