using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Compatibility facade for callers that still require the complete legacy synchronization queue API.
/// New backend services should depend on change enqueueing, user catch-up, or pending activation separately.
/// </summary>
public sealed class SyncQueueService : ISyncQueueService
{
    private readonly ISyncChangeQueueService _changes;
    private readonly IUserSyncCatchUpService _catchUp;
    private readonly IPendingSyncActivationService _activation;

    public SyncQueueService(
        ISyncChangeQueueService changes,
        IUserSyncCatchUpService catchUp,
        IPendingSyncActivationService activation)
    {
        _changes = changes;
        _catchUp = catchUp;
        _activation = activation;
    }

    public Task EnqueueAsync(SyncItem item, CancellationToken ct = default) =>
        _changes.EnqueueAsync(item, ct);

    public Task EnqueueDeferredAsync(SyncItem item, CancellationToken ct = default) =>
        _changes.EnqueueDeferredAsync(item, ct);

    public Task EnqueuePropagationAsync(
        SyncItem item,
        Guid sourceDeviceId,
        long changedAtTs,
        CancellationToken ct = default) =>
        _changes.EnqueuePropagationAsync(item, sourceDeviceId, changedAtTs, ct);

    public Task<bool> TryEnqueueAsync(SyncItem item, CancellationToken ct = default) =>
        _changes.TryEnqueueAsync(item, ct);

    public Task EnqueueForDeviceAsync(
        SyncItem item,
        Guid targetDeviceId,
        CancellationToken ct = default) =>
        _changes.EnqueueForDeviceAsync(item, targetDeviceId, ct);

    public Task EnqueueUserCatchUpAsync(
        Guid userId,
        Guid targetDeviceId,
        CancellationToken ct = default) =>
        _catchUp.EnqueueAsync(userId, targetDeviceId, ct);

    public Task ActivatePendingSyncsAsync(CancellationToken ct = default) =>
        _activation.ActivatePendingAsync(ct);
}
