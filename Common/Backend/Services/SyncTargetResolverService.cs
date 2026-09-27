using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Selects the remote devices eligible to receive a synchronization item.
/// </summary>
public sealed class SyncTargetResolverService : ISyncTargetResolverService
{
    private readonly IGroupRepository _groups;
    private readonly IDeviceRepository _devices;
    private readonly IUserDeviceRepository _userDevices;
    private readonly ILocalUserDeviceRepository _localUserDevices;
    private readonly ILocalDeviceMatcherService _localDevices;

    public SyncTargetResolverService(
        IGroupRepository groups,
        IDeviceRepository devices,
        IUserDeviceRepository userDevices,
        ILocalUserDeviceRepository localUserDevices,
        ILocalDeviceMatcherService localDevices)
    {
        _groups = groups;
        _devices = devices;
        _userDevices = userDevices;
        _localUserDevices = localUserDevices;
        _localDevices = localDevices;
    }

    public async Task<IReadOnlyList<Device>> ResolveTargetsAsync(
        SyncItem item,
        bool touchLocalSyncState,
        IReadOnlyCollection<Guid> excludedDeviceIds,
        CancellationToken ct = default)
    {
        var devices = await ListTargetDevicesAsync(item, touchLocalSyncState, ct);
        var excludedDeviceIdSet = excludedDeviceIds
            .Where(id => id != Guid.Empty)
            .ToHashSet();

        return devices
            .Where(device =>
                !excludedDeviceIdSet.Contains(device.Id) &&
                !_localDevices.IsLocalDevice(device))
            .DistinctBy(device => device.Id)
            .ToList();
    }

    private async Task<IReadOnlyList<Device>> ListTargetDevicesAsync(
        SyncItem item,
        bool touchLocalSyncState,
        CancellationToken ct)
    {
        if (item.ModelType == SyncModelType.User)
            return await ListUserTargetDevicesAsync(item.ModelId, ct);

        if (item.ModelType == SyncModelType.Group)
            return await ListGroupTargetDevicesAsync(item.ModelId, ct);

        if (item.ModelType == SyncModelType.Device)
            return await ListDeviceTargetDevicesAsync(item.ModelId, ct);

        if (item.ModelType == SyncModelType.UserDevice)
        {
            var userDevice = await _userDevices.GetByModelIdAsync(item.ModelId, ct);
            if (userDevice is null)
                return [];

            return await ListUserDeviceChangeTargetDevicesAsync(
                userDevice.UserId,
                userDevice.DeviceId,
                touchLocalSyncState && item.ChangeType == SyncChangeType.Deleted,
                ct);
        }

        return [];
    }

    private async Task<IReadOnlyList<Device>> ListUserTargetDevicesAsync(Guid userId, CancellationToken ct)
    {
        if (!await _localUserDevices.IsSyncOnAsync(userId, ct))
            return [];

        var links = await _userDevices.ListByUserWithDevicesAsync(userId, ct);
        return SelectDistinctDevices(links.Where(link => !link.IsDeleted && link.IsSyncOn));
    }

    private async Task<IReadOnlyList<Device>> ListGroupTargetDevicesAsync(Guid groupId, CancellationToken ct)
    {
        var groupUserIds = await _groups.ListUserIdsAsync(groupId, ct);
        if (groupUserIds.Count == 0)
            return [];

        var locallyEnabledUserIds = (await _localUserDevices.ListSyncOnUserIdsAsync(ct)).ToHashSet();
        var enabledGroupUserIds = groupUserIds
            .Where(locallyEnabledUserIds.Contains)
            .Distinct()
            .ToList();
        if (enabledGroupUserIds.Count == 0)
            return [];

        var links = await _userDevices.ListByUsersWithDevicesAsync(enabledGroupUserIds, ct);
        return SelectDistinctDevices(links.Where(link => !link.IsDeleted && link.IsSyncOn));
    }

    private async Task<IReadOnlyList<Device>> ListDeviceTargetDevicesAsync(Guid sourceDeviceId, CancellationToken ct)
    {
        var sourceLinks = await _userDevices.ListByDeviceAsync(sourceDeviceId, ct);
        var sourceUserIds = sourceLinks
            .Where(link => !link.IsDeleted)
            .Select(link => link.UserId)
            .Distinct()
            .ToHashSet();
        if (sourceUserIds.Count == 0)
            return [];

        var enabledUserIds = (await _localUserDevices.ListSyncOnUserIdsAsync(ct))
            .Where(sourceUserIds.Contains)
            .Distinct()
            .ToList();
        if (enabledUserIds.Count == 0)
            return [];

        var targetLinks = await _userDevices.ListByUsersWithDevicesAsync(enabledUserIds, ct);
        return SelectDistinctDevices(targetLinks.Where(link =>
            link.DeviceId != sourceDeviceId &&
            !link.IsDeleted &&
            link.IsSyncOn));
    }

    private async Task<IReadOnlyList<Device>> ListUserDeviceChangeTargetDevicesAsync(
        Guid userId,
        Guid changedDeviceId,
        bool includeChangedDevice,
        CancellationToken ct)
    {
        if (!await _localUserDevices.IsSyncOnAsync(userId, ct))
            return [];

        var links = await _userDevices.ListByUserWithDevicesAsync(userId, ct);
        return SelectDistinctDevices(links.Where(link =>
            link.DeviceId == changedDeviceId
                ? includeChangedDevice
                : !link.IsDeleted && link.IsSyncOn));
    }

    private IReadOnlyList<Device> SelectDistinctDevices(IEnumerable<UserDevice> links) =>
        links
            .Where(link => link.Device is not null)
            .Select(link => link.Device!)
            .DistinctBy(device => device.Id)
            .ToList();
}
