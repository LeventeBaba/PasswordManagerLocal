using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ISyncQueueWriterService
{
    Task EnqueueAsync(
        SyncItem item,
        long changedAtTs,
        IReadOnlyCollection<Guid> excludedDeviceIds,
        bool touchLocalSyncState,
        bool activateTargets,
        CancellationToken ct = default);

    Task EnqueueForDeviceAsync(SyncItem item, Guid targetDeviceId, CancellationToken ct = default);
}
