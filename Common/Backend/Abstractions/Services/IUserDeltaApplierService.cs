using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserDeltaApplierService
{
    Task<bool> ApplyAsync(SyncDeltaPayload payload, Guid sourceDeviceId, long ts, CancellationToken ct = default);
}
