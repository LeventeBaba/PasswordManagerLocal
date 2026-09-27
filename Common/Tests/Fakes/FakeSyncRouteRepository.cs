using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeSyncRouteRepository : ISyncRouteRepository
{
    private readonly FakeUserDeviceRepository _userDevices;
    private readonly FakeLocalUserDeviceRepository _localUsers;

    public FakeSyncRouteRepository(
        FakeUserDeviceRepository userDevices,
        FakeLocalUserDeviceRepository localUsers)
    {
        _userDevices = userDevices;
        _localUsers = localUsers;
    }

    public Task<bool> IsEligibleAsync(Guid userId, Guid remoteDeviceId, CancellationToken ct = default) =>
        Task.FromResult(userId != Guid.Empty &&
                        remoteDeviceId != Guid.Empty &&
                        EligibleRoutes(remoteDeviceId).Any(link => link.UserId == userId));

    public Task<bool> HasAnyEligibleAsync(IReadOnlyCollection<Guid> userIds, Guid remoteDeviceId, CancellationToken ct = default)
    {
        var ids = NormalizeUserIds(userIds);
        return Task.FromResult(remoteDeviceId != Guid.Empty &&
                               EligibleRoutes(remoteDeviceId).Any(link => ids.Contains(link.UserId)));
    }

    public Task<IReadOnlyList<Guid>> ListEligibleUserIdsAsync(
        IReadOnlyCollection<Guid> userIds,
        Guid remoteDeviceId,
        CancellationToken ct = default)
    {
        var ids = NormalizeUserIds(userIds);
        if (ids.Length == 0 || remoteDeviceId == Guid.Empty)
            return Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

        return Task.FromResult((IReadOnlyList<Guid>)EligibleRoutes(remoteDeviceId)
            .Where(link => ids.Contains(link.UserId))
            .Select(link => link.UserId)
            .Distinct()
            .ToList());
    }

    public Task<bool> HasEligibleUserForDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        Task.FromResult(deviceId != Guid.Empty && EligibleRoutes(deviceId).Any());

    public Task<IReadOnlyList<Guid>> ListAllEligibleUserIdsAsync(Guid remoteDeviceId, CancellationToken ct = default)
    {
        if (remoteDeviceId == Guid.Empty)
            return Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

        return Task.FromResult((IReadOnlyList<Guid>)EligibleRoutes(remoteDeviceId)
            .Select(link => link.UserId)
            .Distinct()
            .ToList());
    }

    private static Guid[] NormalizeUserIds(IReadOnlyCollection<Guid> userIds) =>
        userIds.Where(id => id != Guid.Empty).Distinct().ToArray();

    private IEnumerable<PasswordManagerLocal.Common.Backend.Models.UserDevice> EligibleRoutes(Guid remoteDeviceId)
    {
        var locallyEnabledUserIds = _localUsers.Items
            .Where(local => local.IsSyncOn)
            .Select(local => local.UserId)
            .ToHashSet();

        return _userDevices.Items.Where(link =>
            link.DeviceId == remoteDeviceId &&
            !link.IsDeleted &&
            link.IsSyncOn &&
            locallyEnabledUserIds.Contains(link.UserId));
    }
}
