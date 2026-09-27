using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ISyncTargetResolverService
{
    Task<IReadOnlyList<Device>> ResolveTargetsAsync(
        SyncItem item,
        bool touchLocalSyncState,
        IReadOnlyCollection<Guid> excludedDeviceIds,
        CancellationToken ct = default);
}
