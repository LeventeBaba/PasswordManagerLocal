using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class SyncItemRepository : GenericRepositoryBase<SyncItem>, ISyncItemRepository
{
    public SyncItemRepository(AppDbContext context) : base(context.SyncItems)
    {

    }

    public override Task<SyncItem?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Set.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<int> ClearSyncItemsAsync(CancellationToken ct = default) =>
        await Set.Where(s => !s.QueueItems.Any())
        .ExecuteDeleteAsync(ct);

    public async Task<SyncItem?> GetAsync(Guid modelId, SyncModelType modelType, CancellationToken ct = default) =>
        await Set.FirstOrDefaultAsync(s => s.ModelId == modelId && s.ModelType == modelType, ct);
}