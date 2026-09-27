using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class SyncAuthorizationService : ISyncAuthorizationService
{
    private readonly IGroupRepository _groups;
    private readonly IDeviceRepository _devices;
    private readonly IUserDeviceRepository _userDevices;
    private readonly ILocalUserDeviceRepository _localUsers;
    private readonly ISyncRouteRepository _syncRoutes;
    private readonly IDeviceIdentityService _identity;
    private readonly IUserMembershipAuthorizationRepository? _membershipHistory;
    private readonly IDeletedUserBarrierRepository? _deletionBarriers;

    public SyncAuthorizationService(
        IGroupRepository groups,
        IDeviceRepository devices,
        IUserDeviceRepository userDevices,
        ILocalUserDeviceRepository localUsers,
        ISyncRouteRepository syncRoutes,
        IDeviceIdentityService identity,
        IUserMembershipAuthorizationRepository? membershipHistory = null,
        IDeletedUserBarrierRepository? deletionBarriers = null)
    {
        _groups = groups;
        _devices = devices;
        _userDevices = userDevices;
        _localUsers = localUsers;
        _syncRoutes = syncRoutes;
        _identity = identity;
        _membershipHistory = membershipHistory;
        _deletionBarriers = deletionBarriers;
    }

    public async Task<bool> CanSendAsync(SyncItem item, Guid targetDeviceId, CancellationToken ct = default)
    {
        if (!_identity.IsSyncOn || targetDeviceId == Guid.Empty || targetDeviceId == _identity.LocalDeviceId)
            return false;

        if (item.ModelType == SyncModelType.User)
            return item.ChangeType != SyncChangeType.Deleted && await _syncRoutes.IsEligibleAsync(item.ModelId, targetDeviceId, ct);

        if (item.ModelType == SyncModelType.UserDevice)
        {
            var link = await _userDevices.GetByModelIdAsync(item.ModelId, ct);
            if (link is null || !await _localUsers.IsSyncOnAsync(link.UserId, ct))
                return false;

            if (link.DeviceId == targetDeviceId)
                return item.ChangeType == SyncChangeType.Deleted && link.IsDeleted;

            return await _syncRoutes.IsEligibleAsync(link.UserId, targetDeviceId, ct);
        }

        if (item.ModelType == SyncModelType.Group)
        {
            var userIds = await _groups.ListUserIdsAsync(item.ModelId, ct);
            return await _syncRoutes.HasAnyEligibleAsync(userIds, targetDeviceId, ct);
        }

        if (item.ModelType == SyncModelType.Device)
        {
            var userIds = await _devices.ListActiveUserIdsAsync(item.ModelId, ct);
            return await _syncRoutes.HasAnyEligibleAsync(userIds, targetDeviceId, ct);
        }

        return false;
    }

    public async Task<bool> CanReceiveAsync(SyncDeltaPayload payload, Guid sourceDeviceId, CancellationToken ct = default)
    {
        if (!_identity.IsSyncOn || sourceDeviceId == Guid.Empty || sourceDeviceId == _identity.LocalDeviceId)
            return false;

        if (payload.ModelType == SyncModelType.User)
        {
            if (payload.ChangeType == SyncChangeType.Deleted)
                return false;

            var isDeletionOperation = payload.UserControlOperation?.OperationType == UserControlOperationType.AccountDeletion;
            var isDeletedLocally = _deletionBarriers is not null && await _deletionBarriers.ExistsAsync(payload.ModelId, ct);
            if ((isDeletionOperation || isDeletedLocally) && _membershipHistory is not null)
                return await _membershipHistory.HasHistoricalAuthorizationAsync(payload.ModelId, sourceDeviceId, ct);

            return await _syncRoutes.IsEligibleAsync(payload.ModelId, sourceDeviceId, ct);
        }

        if (payload.ModelType == SyncModelType.UserDevice)
        {
            if (payload.UserDevice is null || !await _localUsers.IsSyncOnAsync(payload.UserDevice.UserId, ct))
                return false;

            if (payload.UserDevice.DeviceId == _identity.LocalDeviceId)
                return payload.ChangeType == SyncChangeType.Deleted || payload.UserDevice.IsDeleted;

            return await _syncRoutes.IsEligibleAsync(payload.UserDevice.UserId, sourceDeviceId, ct);
        }

        if (payload.ModelType == SyncModelType.Group)
        {
            IReadOnlyCollection<Guid> userIds = payload.Group?.UserIds ?? [];
            if (userIds.Count == 0)
                userIds = await _groups.ListUserIdsAsync(payload.ModelId, ct);

            return await _syncRoutes.HasAnyEligibleAsync(userIds, sourceDeviceId, ct);
        }

        if (payload.ModelType == SyncModelType.Device)
        {
            IReadOnlyCollection<Guid> userIds = payload.Device?.UserIds ?? [];
            if (userIds.Count == 0)
                userIds = await _devices.ListActiveUserIdsAsync(payload.ModelId, ct);

            return await _syncRoutes.HasAnyEligibleAsync(userIds, sourceDeviceId, ct);
        }

        return false;
    }

    public Task<bool> HasEligibleUserForDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        _syncRoutes.HasEligibleUserForDeviceAsync(deviceId, ct);
}
