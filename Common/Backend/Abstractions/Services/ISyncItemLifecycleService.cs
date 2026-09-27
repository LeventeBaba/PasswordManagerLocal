using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ISyncItemLifecycleService
{
    Task<SyncItem> GetOrCreateAsync(SyncItem item, long changedAtTs, CancellationToken ct = default);
    Task TouchLocalStateAsync(SyncItem item, long changedAtTs, CancellationToken ct = default);
}
