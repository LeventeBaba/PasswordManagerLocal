using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IDeviceSyncTaskService
{
    bool TryStart(DiscoveredDeviceEndpoint endpoint, Device device);
    Task WaitForIdleAsync(Guid deviceId, CancellationToken ct = default);
    Task StopAllAsync(CancellationToken ct = default);
}
