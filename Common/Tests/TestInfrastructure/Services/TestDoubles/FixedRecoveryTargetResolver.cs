using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;

internal sealed class FixedRecoveryTargetResolver(Device target) : ISyncTargetResolverService
{
    public Task<IReadOnlyList<Device>> ResolveTargetsAsync(SyncItem item, bool touchLocalSyncState,
        IReadOnlyCollection<Guid> excludedDeviceIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Device>>(excludedDeviceIds.Contains(target.Id) ? [] : [target]);
}
