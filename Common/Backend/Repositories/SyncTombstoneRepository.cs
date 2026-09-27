using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using static PasswordManagerLocal.Common.Backend.Constants.TombstoneConstants;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class SyncTombstoneRepository : ISyncTombstoneRepository
{
    private readonly AppDbContext _context;
    private readonly DbSet<SyncTombstone> _set;

    public SyncTombstoneRepository(AppDbContext context)
    {
        _context = context;
        _set = context.SyncTombstones;
    }




    public async Task<SyncTombstone?> GetAsync(Guid modelId, SyncModelType modelType, CancellationToken ct = default)
    {
        var local = _set.Local.FirstOrDefault(tombstone =>
            _context.Entry(tombstone).State != EntityState.Deleted &&
            tombstone.ModelId == modelId &&
            tombstone.ModelType == modelType);
        if (local is not null)
            return local;

        return await _set.FirstOrDefaultAsync(x => x.ModelId == modelId && x.ModelType == modelType, ct);
    }


    public async Task UpsertAsync(Guid modelId, SyncModelType modelType, long deletedAtTs, CancellationToken ct = default)
    {
        var existing = await GetAsync(modelId, modelType, ct);
        if (existing is not null)
        {
            if (deletedAtTs > existing.DeletedAtTs)
                existing.DeletedAtTs = deletedAtTs;

            return;
        }

        await ThrowIfTombstoneCapacityExceededAsync(ct);

        await _set.AddAsync(new SyncTombstone
        {
            ModelId = modelId,
            ModelType = modelType,
            DeletedAtTs = deletedAtTs
        }, ct);
    }


    public void Delete(SyncTombstone tombstone) =>
        _set.Remove(tombstone);


    private async Task ThrowIfTombstoneCapacityExceededAsync(CancellationToken ct)
    {
        if (MaxSyncTombstones < 1)
            throw new InvalidOperationException("Synchronization tombstone storage is disabled; deletion knowledge cannot be safely recorded.");

        var persistedCount = await _set.CountAsync(ct);
        var pendingAdds = _set.Local.Count(tombstone => _context.Entry(tombstone).State == EntityState.Added);
        if (persistedCount + pendingAdds >= MaxSyncTombstones)
        {
            throw new InvalidOperationException(
                "The synchronization tombstone safety limit was reached. Tombstones were preserved to prevent data resurrection; manual maintenance or causal garbage collection is required.");
        }
    }

}
