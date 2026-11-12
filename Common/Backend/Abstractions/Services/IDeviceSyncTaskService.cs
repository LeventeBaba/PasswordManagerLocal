using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IDeviceSyncTaskService
{
    bool TryStart(DiscoveredDeviceEndpoint endpoint, Device device);

    // Discovery is a periodic observation, not a request for immediate reconciliation.
    // Keep the default implementation for alternate/test implementations of the interface.
    bool TryStartFromDiscovery(DiscoveredDeviceEndpoint endpoint, Device device) => TryStart(endpoint, device);
    Task WaitForIdleAsync(Guid deviceId, CancellationToken ct = default);
    Task StopAllAsync(CancellationToken ct = default);
}
