using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using static PasswordManagerLocal.Common.Backend.Constants.TombstoneConstants;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeSyncTombstoneRepository : ISyncTombstoneRepository
{
    private readonly Dictionary<(Guid ModelId, SyncModelType ModelType), SyncTombstone> _items = [];

    public IReadOnlyCollection<SyncTombstone> Items => _items.Values;

    public Task<SyncTombstone?> GetAsync(Guid modelId, SyncModelType modelType, CancellationToken ct = default) =>
        Task.FromResult(_items.GetValueOrDefault((modelId, modelType)));

    public Task UpsertAsync(Guid modelId, SyncModelType modelType, long deletedAtTs, CancellationToken ct = default)
    {
        var key = (modelId, modelType);
        if (_items.TryGetValue(key, out var existing))
        {
            if (deletedAtTs > existing.DeletedAtTs)
                existing.DeletedAtTs = deletedAtTs;

            return Task.CompletedTask;
        }

        RemoveOldestTombstonesToMakeRoom();
        _items[key] = new SyncTombstone
        {
            ModelId = modelId,
            ModelType = modelType,
            DeletedAtTs = deletedAtTs
        };
        return Task.CompletedTask;
    }

    public void Delete(SyncTombstone tombstone) =>
        _items.Remove((tombstone.ModelId, tombstone.ModelType));


    private void RemoveOldestTombstonesToMakeRoom()
    {
        if (MaxSyncTombstones < 1 || _items.Count < MaxSyncTombstones)
            return;

        var oldest = _items.Values
            .OrderBy(tombstone => tombstone.DeletedAtTs)
            .ThenBy(tombstone => tombstone.Id)
            .FirstOrDefault();
        if (oldest is not null)
            _items.Remove((oldest.ModelId, oldest.ModelType));
    }
}
