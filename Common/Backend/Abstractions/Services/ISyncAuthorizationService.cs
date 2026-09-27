using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ISyncAuthorizationService
{
    Task<bool> CanSendAsync(SyncItem item, Guid targetDeviceId, CancellationToken ct = default);
    Task<bool> CanReceiveAsync(SyncDeltaPayload payload, Guid sourceDeviceId, CancellationToken ct = default);
    Task<bool> HasEligibleUserForDeviceAsync(Guid deviceId, CancellationToken ct = default);
}
