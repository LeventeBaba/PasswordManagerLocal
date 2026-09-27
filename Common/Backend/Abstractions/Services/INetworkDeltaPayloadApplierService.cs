using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface INetworkDeltaPayloadApplierService
{
    Task<bool> ApplyAsync(SyncDeltaPayload payload, Guid sourceDeviceId, long ts, CancellationToken ct = default);
}
