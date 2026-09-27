using PasswordManagerLocal.Common.Backend.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

public interface ISyncItemRepository : IGenericRepository<SyncItem>
{
    Task<int> ClearSyncItemsAsync(CancellationToken ct = default);
    Task<SyncItem?> GetAsync(Guid modelId, SyncModelType modelType, CancellationToken ct = default);
}