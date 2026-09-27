using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

public interface ISyncTombstoneRepository
{
    Task<SyncTombstone?> GetAsync(Guid modelId, SyncModelType modelType, CancellationToken ct = default);
    Task UpsertAsync(Guid modelId, SyncModelType modelType, long deletedAtTs, CancellationToken ct = default);
    void Delete(SyncTombstone tombstone);
}
