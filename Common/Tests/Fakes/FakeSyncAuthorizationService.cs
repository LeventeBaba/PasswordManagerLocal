using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeSyncAuthorizationService : ISyncAuthorizationService
{
    public bool CanSend { get; set; } = true;
    public bool CanReceive { get; set; } = true;
    public bool HasEligibleUser { get; set; } = true;

    public Task<bool> CanSendAsync(SyncItem item, Guid targetDeviceId, CancellationToken ct = default) =>
        Task.FromResult(CanSend);

    public Task<bool> CanReceiveAsync(SyncDeltaPayload payload, Guid sourceDeviceId, CancellationToken ct = default) =>
        Task.FromResult(CanReceive);

    public Task<bool> HasEligibleUserForDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        Task.FromResult(HasEligibleUser);
}
