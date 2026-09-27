using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeSyncQueueRepository : ISyncQueueRepository
{
    private readonly object _gate = new();
    private readonly List<SyncQueueItem> _items = [];

    public IReadOnlyList<SyncQueueItem> Items
    {
        get
        {
            lock (_gate)
                return _items.ToList();
        }
    }

    public Task<bool> ExistsAsync(Guid syncItemId, Guid deviceId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_items.Any(item => item.SyncItemId == syncItemId && item.DeviceId == deviceId));
    }

    public Task<bool> HasPendingForDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_items.Any(item => item.DeviceId == deviceId && item.ProcessedAt is null));
    }

    public Task<bool> HasPendingForSyncItemAsync(Guid syncItemId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_items.Any(item => item.SyncItemId == syncItemId && item.ProcessedAt is null));
    }

    public Task<bool> HasPendingForModelAsync(Guid modelId, SyncModelType modelType, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_items.Any(item =>
                item.ProcessedAt is null &&
                item.SyncItem is not null &&
                item.SyncItem.ModelId == modelId &&
                item.SyncItem.ModelType == modelType));
    }

    public Task<SyncQueueItem?> GetNextPendingForDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_items
                .Where(item => item.DeviceId == deviceId && item.ProcessedAt is null)
                .OrderBy(item => item.QueueId)
                .FirstOrDefault());
        }
    }

    public Task<IReadOnlyList<SyncQueueItem>> GetNextPendingBatchForDeviceAsync(Guid deviceId, int limit, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult((IReadOnlyList<SyncQueueItem>)_items
                .Where(item => item.DeviceId == deviceId && item.ProcessedAt is null)
                .OrderBy(item => item.QueueId)
                .Take(Math.Max(0, limit))
                .ToList());
        }
    }

    public Task<IReadOnlyList<SyncQueueItem>> ListPendingForDeviceWithItemsAsync(Guid deviceId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult((IReadOnlyList<SyncQueueItem>)_items
                .Where(item => item.DeviceId == deviceId && item.ProcessedAt is null)
                .OrderBy(item => item.QueueId)
                .ToList());
        }
    }

    public Task<IReadOnlyList<Guid>> ListQueuedDeviceIdsAsync(Guid syncItemId, IReadOnlyList<Guid> deviceIds, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult((IReadOnlyList<Guid>)_items
                .Where(item => item.SyncItemId == syncItemId && deviceIds.Contains(item.DeviceId))
                .Select(item => item.DeviceId)
                .Distinct()
                .ToList());
        }
    }

    public Task EnqueueAsync(Guid syncItemId, Guid deviceId, CancellationToken ct = default)
    {
        lock (_gate)
            AddUnsafe(new SyncQueueItem { SyncItemId = syncItemId, DeviceId = deviceId });
        return Task.CompletedTask;
    }

    public Task<bool> TryEnqueueAsync(Guid syncItemId, Guid deviceId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_items.Any(item => item.SyncItemId == syncItemId && item.DeviceId == deviceId))
                return Task.FromResult(false);

            AddUnsafe(new SyncQueueItem { SyncItemId = syncItemId, DeviceId = deviceId });
            return Task.FromResult(true);
        }
    }

    public Task EnqueueAsync(IEnumerable<SyncQueueItem> items, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var item in items)
                AddUnsafe(item);
        }
        return Task.CompletedTask;
    }

    public void Update(SyncQueueItem item)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(existing => existing.Id == item.Id);
            if (index >= 0)
                _items[index] = item;
        }
    }

    public void Delete(SyncQueueItem item)
    {
        lock (_gate)
            _items.RemoveAll(existing => existing.Id == item.Id);
    }

    public void Seed(params SyncQueueItem[] items)
    {
        lock (_gate)
        {
            foreach (var item in items)
                AddUnsafe(item);
        }
    }

    private void AddUnsafe(SyncQueueItem item)
    {
        if (_items.Any(existing => existing.SyncItemId == item.SyncItemId && existing.DeviceId == item.DeviceId))
            return;

        if (item.QueueId == 0)
            item.QueueId = _items.Count == 0 ? 1 : _items.Max(existing => existing.QueueId) + 1;
        _items.Add(item);
    }
}
