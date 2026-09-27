using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeSyncQueueService :
    ISyncQueueService,
    ISyncChangeQueueService,
    IUserSyncCatchUpService,
    IPendingSyncActivationService
{
    public List<SyncItem> EnqueuedItems { get; } = [];
    public List<SyncItem> DeferredEnqueuedItems { get; } = [];
    public List<(SyncItem Item, Guid TargetDeviceId)> EnqueuedForDevices { get; } = [];
    public List<(Guid UserId, Guid TargetDeviceId)> UserCatchUpRequests { get; } = [];
    public int ActivatePendingSyncsCalls { get; private set; }
    public List<Device> ActivatedDevices { get; } = [];

    public Task EnqueueAsync(SyncItem item, CancellationToken ct = default)
    {
        EnqueuedItems.Add(item);
        return Task.CompletedTask;
    }

    public Task EnqueueDeferredAsync(SyncItem item, CancellationToken ct = default)
    {
        EnqueuedItems.Add(item);
        DeferredEnqueuedItems.Add(item);
        return Task.CompletedTask;
    }

    public Task EnqueuePropagationAsync(SyncItem item, Guid sourceDeviceId, long changedAtTs, CancellationToken ct = default)
    {
        item.ChangedAtTs = changedAtTs;
        EnqueuedItems.Add(item);
        return Task.CompletedTask;
    }

    public Task<bool> TryEnqueueAsync(SyncItem item, CancellationToken ct = default)
    {
        EnqueuedItems.Add(item);
        return Task.FromResult(true);
    }

    public Task EnqueueForDeviceAsync(SyncItem item, Guid targetDeviceId, CancellationToken ct = default)
    {
        EnqueuedItems.Add(item);
        EnqueuedForDevices.Add((item, targetDeviceId));
        return Task.CompletedTask;
    }

    public Task EnqueueAsync(Guid userId, Guid targetDeviceId, CancellationToken ct = default)
    {
        UserCatchUpRequests.Add((userId, targetDeviceId));
        return Task.CompletedTask;
    }

    public Task EnqueueUserCatchUpAsync(Guid userId, Guid targetDeviceId, CancellationToken ct = default) =>
        EnqueueAsync(userId, targetDeviceId, ct);

    public void ActivateDevices(IReadOnlyList<Device> devices)
    {
        ActivatedDevices.AddRange(devices);
    }

    public Task ActivatePendingAsync(CancellationToken ct = default)
    {
        ActivatePendingSyncsCalls++;
        return Task.CompletedTask;
    }

    public Task ActivatePendingSyncsAsync(CancellationToken ct = default) =>
        ActivatePendingAsync(ct);
}
