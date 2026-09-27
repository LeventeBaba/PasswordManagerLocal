using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeDeviceSyncTaskService : IDeviceSyncTaskService
{
    public List<(DiscoveredDeviceEndpoint Endpoint, Device Device)> Starts { get; } = [];
    public int StopAllCalls { get; private set; }

    public bool TryStart(DiscoveredDeviceEndpoint endpoint, Device device)
    {
        Starts.Add((endpoint, device));
        return true;
    }

    public Task WaitForIdleAsync(Guid deviceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task StopAllAsync(CancellationToken ct = default)
    {
        StopAllCalls++;
        return Task.CompletedTask;
    }
}
