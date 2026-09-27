using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ISyncService
{
    Task NeedsSyncAsync(Guid modelId, SyncModelType modelType, SyncChangeType changeType, CancellationToken ct = default);
}