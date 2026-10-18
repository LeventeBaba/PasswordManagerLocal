using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class SyncQueueRepository : ISyncQueueRepository
{
    private static readonly object EnqueuedAtGenerationGate = new();
    private static DateTimeOffset _lastEnqueuedAtGeneration = DateTimeOffset.UtcNow;
    private readonly AppDbContext _context;
    private readonly DbSet<SyncQueueItem> _queue;

    public SyncQueueRepository(AppDbContext context)
    {
        _context = context;
        _queue = context.SyncQueueItems;
    }




    public async Task<bool> ExistsAsync(Guid syncItemId, Guid deviceId, CancellationToken ct = default) =>
        await _queue.AnyAsync(x => x.SyncItemId == syncItemId && x.DeviceId == deviceId && x.ProcessedAt == null, ct);


    public async Task<bool> HasPendingForDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        await _queue.AsNoTracking().AnyAsync(x => x.DeviceId == deviceId && x.ProcessedAt == null, ct);


    public async Task<bool> HasPendingForSyncItemAsync(Guid syncItemId, CancellationToken ct = default) =>
        await _queue.AsNoTracking().AnyAsync(x => x.SyncItemId == syncItemId && x.ProcessedAt == null, ct);


    public async Task<bool> HasPendingForModelAsync(Guid modelId, SyncModelType modelType, CancellationToken ct = default) =>
        await _queue.AsNoTracking()
            .AnyAsync(x => x.ProcessedAt == null &&
                           x.SyncItem != null &&
                           x.SyncItem.ModelId == modelId &&
                           x.SyncItem.ModelType == modelType, ct);


    public async Task<SyncQueueItem?> GetNextPendingForDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        await _queue
            .Include(x => x.SyncItem)
            .Where(x => x.DeviceId == deviceId && x.ProcessedAt == null)
            .OrderBy(x => x.QueueId)
            .FirstOrDefaultAsync(ct);


    public async Task<IReadOnlyList<SyncQueueItem>> GetNextPendingBatchForDeviceAsync(Guid deviceId, int limit, CancellationToken ct = default)
    {
        if (limit <= 0)
            return [];

        return await _queue
            .Include(x => x.SyncItem)
            .Where(x => x.DeviceId == deviceId && x.ProcessedAt == null)
            .OrderBy(x => x.QueueId)
            .Take(limit)
            .ToListAsync(ct);
    }


    public async Task<IReadOnlyList<SyncQueueItem>> ListPendingForDeviceWithItemsAsync(Guid deviceId, CancellationToken ct = default) =>
        await _queue
            .Include(x => x.SyncItem)
            .Where(x => x.DeviceId == deviceId && x.ProcessedAt == null)
            .OrderBy(x => x.QueueId)
            .ToListAsync(ct);



    public async Task<IReadOnlyList<Guid>> ListQueuedDeviceIdsAsync(Guid syncItemId, IReadOnlyList<Guid> deviceIds, CancellationToken ct = default)
    {
        if (deviceIds.Count == 0)
            return [];

        return await _queue.AsNoTracking()
            .Where(x => x.SyncItemId == syncItemId && deviceIds.Contains(x.DeviceId) && x.ProcessedAt == null)
            .Select(x => x.DeviceId)
            .ToListAsync(ct);
    }


    public async Task EnqueueAsync(Guid syncItemId, Guid deviceId, CancellationToken ct = default)
    {
        var existing = await _queue.FirstOrDefaultAsync(x => x.SyncItemId == syncItemId && x.DeviceId == deviceId, ct);
        if (existing is not null)
        {
            existing.ProcessedAt = null;
            _context.Entry(existing).Property(item => item.ProcessedAt).IsModified = true;
            existing.EnqueuedAt = NextEnqueuedAt(existing.EnqueuedAt);
            return;
        }

        var newItem = new SyncQueueItem
        {
            SyncItemId = syncItemId,
            DeviceId = deviceId
        };

        await _queue.AddAsync(newItem, ct);
    }


    public async Task<bool> TryEnqueueAsync(Guid syncItemId, Guid deviceId, CancellationToken ct = default)
    {
        try
        {
            await EnqueueAsync(syncItemId, deviceId, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }


    public async Task EnqueueAsync(IEnumerable<SyncQueueItem> items, CancellationToken ct = default)
    {
        var itemList = items.ToList();
        if (itemList.Count == 0)
            return;

        var syncItemIds = itemList.Select(x => x.SyncItemId).Distinct().ToList();
        var deviceIds = itemList.Select(x => x.DeviceId).Distinct().ToList();

        var existingItems = await _queue
            .Where(x => syncItemIds.Contains(x.SyncItemId) && deviceIds.Contains(x.DeviceId))
            .ToListAsync(ct);

        var existingKeys = existingItems
            .Select(x => (x.SyncItemId, x.DeviceId))
            .ToHashSet();

        foreach (var existingItem in existingItems)
        {
            existingItem.ProcessedAt = null;
            _context.Entry(existingItem).Property(item => item.ProcessedAt).IsModified = true;
            existingItem.EnqueuedAt = NextEnqueuedAt(existingItem.EnqueuedAt);
        }

        var newItems = itemList
            .Where(x => !existingKeys.Contains((x.SyncItemId, x.DeviceId)))
            .ToList();

        if (newItems.Count != 0)
            await _queue.AddRangeAsync(newItems, ct);
    }


    public async Task<bool> TryAcknowledgeAsync(
        Guid queueItemId,
        DateTimeOffset expectedEnqueuedAt,
        CancellationToken ct = default)
    {
        DateTimeOffset? processedAt = DateTimeOffset.UtcNow;
        return await _queue
            .Where(item =>
                item.Id == queueItemId &&
                item.ProcessedAt == null &&
                item.EnqueuedAt == expectedEnqueuedAt)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.ProcessedAt, processedAt),
                ct) == 1;
    }


    private static DateTimeOffset NextEnqueuedAt(DateTimeOffset current)
    {
        lock (EnqueuedAtGenerationGate)
        {
            var next = DateTimeOffset.UtcNow;
            if (next <= current)
                next = current.AddTicks(1);
            if (next <= _lastEnqueuedAtGeneration)
                next = _lastEnqueuedAtGeneration.AddTicks(1);

            _lastEnqueuedAtGeneration = next;
            return next;
        }
    }


    public void Update(SyncQueueItem item) =>
        _queue.Update(item);


    public void Delete(SyncQueueItem item) =>
        _queue.Remove(item);
}
