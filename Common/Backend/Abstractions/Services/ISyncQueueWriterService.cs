using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ISyncQueueWriterService
{
    /// <summary>
    /// Coalesces a change and queues eligible targets. Exclusions prevent new entries,
    /// preserve existing/in-flight work, and are ignored for a newer local revision.
    /// </summary>
    Task EnqueueAsync(
        SyncItem item,
        long changedAtTs,
        IReadOnlyCollection<Guid> excludedDeviceIds,
        bool touchLocalSyncState,
        bool activateTargets,
        CancellationToken ct = default);

    Task EnqueueForDeviceAsync(SyncItem item, Guid targetDeviceId, CancellationToken ct = default);
}
