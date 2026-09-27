using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ISyncQueueService
{
    Task EnqueueAsync(SyncItem item, CancellationToken ct = default);
    /// <summary>Persists queue work without starting discovery or delivery. The caller must activate pending syncs after its transaction commits.</summary>
    Task EnqueueDeferredAsync(SyncItem item, CancellationToken ct = default);
    Task EnqueuePropagationAsync(SyncItem item, Guid sourceDeviceId, long changedAtTs, CancellationToken ct = default);
    Task<bool> TryEnqueueAsync(SyncItem item, CancellationToken ct = default);
    Task EnqueueForDeviceAsync(SyncItem item, Guid targetDeviceId, CancellationToken ct = default);
    Task EnqueueUserCatchUpAsync(Guid userId, Guid targetDeviceId, CancellationToken ct = default);
    /// <summary>Rehydrates discovery identities and starts registered endpoints for all committed pending queue work.</summary>
    Task ActivatePendingSyncsAsync(CancellationToken ct = default);
}
