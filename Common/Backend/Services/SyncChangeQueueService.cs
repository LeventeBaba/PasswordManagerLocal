using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Exposes semantic synchronization-change enqueue operations while delegating persistence details to the queue writer.
/// </summary>
public sealed class SyncChangeQueueService : ISyncChangeQueueService
{
    private readonly ISyncQueueWriterService _writer;

    public SyncChangeQueueService(ISyncQueueWriterService writer)
    {
        _writer = writer;
    }

    public Task EnqueueAsync(SyncItem item, CancellationToken ct = default) =>
        _writer.EnqueueAsync(
            item,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            [],
            touchLocalSyncState: true,
            activateTargets: true,
            ct: ct);

    public Task EnqueueDeferredAsync(SyncItem item, CancellationToken ct = default) =>
        _writer.EnqueueAsync(
            item,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            [],
            touchLocalSyncState: true,
            activateTargets: false,
            ct: ct);

    public Task EnqueuePropagationAsync(
        SyncItem item,
        Guid sourceDeviceId,
        long changedAtTs,
        CancellationToken ct = default) =>
        _writer.EnqueueAsync(
            item,
            changedAtTs,
            [sourceDeviceId],
            touchLocalSyncState: false,
            activateTargets: true,
            ct: ct);

    public async Task<bool> TryEnqueueAsync(SyncItem item, CancellationToken ct = default)
    {
        try
        {
            await EnqueueAsync(item, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task EnqueueForDeviceAsync(
        SyncItem item,
        Guid targetDeviceId,
        CancellationToken ct = default) =>
        _writer.EnqueueForDeviceAsync(item, targetDeviceId, ct);
}
