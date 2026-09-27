using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface INetworkDeltaLifecycleService
{
    Task TouchSourceDeviceAsync(Device sourceDevice, CancellationToken ct = default);
    Task BeforeSaveAsync(SyncDeltaPayload payload, Device sourceDevice, bool applied, long ts, CancellationToken ct = default);
    Task AfterSaveAsync(SyncDeltaPayload payload, Device sourceDevice, bool applied, long ts, CancellationToken ct = default);
}
