using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Persists synchronization queue entries after item lifecycle and target-resolution decisions are complete.
/// </summary>
public sealed class SyncQueueWriterService : ISyncQueueWriterService
{
    private readonly ISyncQueueRepository _syncQueue;
    private readonly ISyncItemLifecycleService _syncItems;
    private readonly ISyncTargetResolverService _targets;
    private readonly IPendingSyncActivationService _activation;
    private readonly IDeviceRepository _devices;
    private readonly IDeviceIdentityService _identity;
    private readonly ISyncAuthorizationService _authorization;
    private readonly IUnitOfWork _uow;

    public SyncQueueWriterService(
        ISyncQueueRepository syncQueue,
        ISyncItemLifecycleService syncItems,
        ISyncTargetResolverService targets,
        IPendingSyncActivationService activation,
        IDeviceRepository devices,
        IDeviceIdentityService identity,
        ISyncAuthorizationService authorization,
        IUnitOfWork uow)
    {
        _syncQueue = syncQueue;
        _syncItems = syncItems;
        _targets = targets;
        _activation = activation;
        _devices = devices;
        _identity = identity;
        _authorization = authorization;
        _uow = uow;
    }

    public async Task EnqueueAsync(
        SyncItem item,
        long changedAtTs,
        IReadOnlyCollection<Guid> excludedDeviceIds,
        bool touchLocalSyncState,
        bool activateTargets,
        CancellationToken ct = default)
    {
        item.ChangedAtTs = changedAtTs;
        var syncItem = await _syncItems.GetOrCreateAsync(item, changedAtTs, ct);

        if (touchLocalSyncState)
            await _syncItems.TouchLocalStateAsync(syncItem, changedAtTs, ct);

        var effectiveExcludedDeviceIds = syncItem.ChangedAtTs > changedAtTs
            ? Array.Empty<Guid>()
            : excludedDeviceIds;
        var targetDevices = await _targets.ResolveTargetsAsync(
            syncItem,
            touchLocalSyncState,
            effectiveExcludedDeviceIds,
            ct);

        await EnqueueMissingTargetsAsync(syncItem, targetDevices, ct);

        await _uow.SaveChangesAsync(ct);

        if (activateTargets)
        {
            try
            {
                _activation.ActivateDevices(targetDevices);
            }
            catch
            {
                // Persisted queue rows remain authoritative; activation is only a wake-up optimization.
            }
        }
    }

    public async Task EnqueueForDeviceAsync(
        SyncItem item,
        Guid targetDeviceId,
        CancellationToken ct = default)
    {
        if (targetDeviceId == Guid.Empty || targetDeviceId == _identity.LocalDeviceId)
            return;

        var changedAtTs = item.ChangedAtTs > 0
            ? item.ChangedAtTs
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        item.ChangedAtTs = changedAtTs;
        var syncItem = await _syncItems.GetOrCreateAsync(item, changedAtTs, ct);

        if (!await _authorization.CanSendAsync(syncItem, targetDeviceId, ct))
        {
            await _uow.SaveChangesAsync(ct);
            return;
        }

        var existing = await _syncQueue.ListQueuedDeviceIdsAsync(syncItem.Id, [targetDeviceId], ct);
        if (existing.Count == 0)
        {
            await _syncQueue.EnqueueAsync(
                [new SyncQueueItem { DeviceId = targetDeviceId, SyncItemId = syncItem.Id }],
                ct);
        }

        await _uow.SaveChangesAsync(ct);

        try
        {
            var target = await _devices.GetByIdAsync(targetDeviceId, ct);
            if (target is not null)
                _activation.ActivateDevices([target]);
        }
        catch
        {
            // Persisted queue rows remain authoritative; activation is only a wake-up optimization.
        }
    }

    private async Task EnqueueMissingTargetsAsync(
        SyncItem syncItem,
        IReadOnlyList<Device> targetDevices,
        CancellationToken ct)
    {
        if (targetDevices.Count == 0)
            return;

        var deviceIds = targetDevices
            .Select(device => device.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (deviceIds.Count == 0)
            return;

        var queuedDeviceIds = await _syncQueue.ListQueuedDeviceIdsAsync(syncItem.Id, deviceIds, ct);
        var queuedDeviceIdSet = queuedDeviceIds.ToHashSet();
        var queueItems = deviceIds
            .Where(id => !queuedDeviceIdSet.Contains(id))
            .Select(id => new SyncQueueItem
            {
                DeviceId = id,
                SyncItemId = syncItem.Id
            })
            .ToList();

        if (queueItems.Count != 0)
            await _syncQueue.EnqueueAsync(queueItems, ct);
    }
}
