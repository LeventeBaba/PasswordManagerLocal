using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Internal.Devices;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class UserDeviceSettingsService : IUserDeviceSettingsService
{
    private readonly IUserLookupService _userLookup;
    private readonly IDeviceIdentityService _identity;
    private readonly IDeviceRepository _devices;
    private readonly IUserDeviceRepository _userDevices;
    private readonly ISyncRouteRepository _syncRoutes;
    private readonly ISyncChangeQueueService _syncChanges;
    private readonly IUserSyncCatchUpService _userSyncCatchUp;
    private readonly ISyncQueueRepository _syncQueueItems;
    private readonly ISyncDeviceIdentityService _syncDeviceIdentities;
    private readonly ILocalDeviceSettingsService _localDeviceSettings;
    private readonly UserDeviceMetadataEditor _metadataEditor;
    private readonly UserDeviceAccessor _accessor;

    public UserDeviceSettingsService(
        IUserLookupService userLookup,
        IDeviceIdentityService identity,
        IDeviceRepository devices,
        IUserDeviceRepository userDevices,
        ISyncRouteRepository syncRoutes,
        ISyncChangeQueueService syncChanges,
        IUserSyncCatchUpService userSyncCatchUp,
        ISyncQueueRepository syncQueueItems,
        ISyncDeviceIdentityService syncDeviceIdentities,
        ILocalDeviceSettingsService localDeviceSettings,
        UserDeviceMetadataEditor metadataEditor,
        UserDeviceAccessor accessor)
    {
        _userLookup = userLookup;
        _identity = identity;
        _devices = devices;
        _userDevices = userDevices;
        _syncRoutes = syncRoutes;
        _syncChanges = syncChanges;
        _userSyncCatchUp = userSyncCatchUp;
        _syncQueueItems = syncQueueItems;
        _syncDeviceIdentities = syncDeviceIdentities;
        _localDeviceSettings = localDeviceSettings;
        _metadataEditor = metadataEditor;
        _accessor = accessor;
    }

    public Task SetUserDeviceNameAsync(Guid token, Guid deviceId, string name, CancellationToken ct = default) =>
        _metadataEditor.SetNameAsync(token, deviceId, name, ct);

    public async Task SetUserDeviceSyncOnAsync(Guid token, Guid deviceId, bool isSyncOn, CancellationToken ct = default)
    {
        if (deviceId == _identity.LocalDeviceId)
        {
            await _localDeviceSettings.SetLocalUserSyncOnAsync(token, isSyncOn, ct);
            return;
        }

        var user = await _userLookup.GetAndVerifyUserAsync(token, ct);
        var userDevice = await _accessor.GetActiveRemoteAsync(user.UId, deviceId, ct);
        if (userDevice.IsSyncOn == isSyncOn)
        {
            if (!isSyncOn)
                await RemoveCachedDeviceIfNoPendingAsync(userDevice, ct);
            return;
        }

        userDevice.IsSyncOn = isSyncOn;
        userDevice.LastModifiedAt = DateTimeOffset.UtcNow;
        userDevice.GenerateIntegrityHash();
        _userDevices.Update(userDevice);
        await EnqueueUserDeviceChangeAsync(userDevice, SyncChangeType.Updated, ct);

        try
        {
            if (isSyncOn)
                await _userSyncCatchUp.EnqueueAsync(user.UId, deviceId, ct);
            else
                await RemoveCachedDeviceIfNoPendingAsync(userDevice, ct);
        }
        catch (Exception ex)
        {
            throw new MutationPartiallyCommittedException(
                "The remote-device synchronization setting was committed, but follow-up synchronization work did not complete.",
                innerException: ex);
        }
    }

    public async Task UnblockUserDeviceAsync(Guid token, Guid deviceId, CancellationToken ct = default)
    {
        var user = await _userLookup.GetAndVerifyUserAsync(token, ct);
        await _accessor.GetActiveRemoteAsync(user.UId, deviceId, ct);
        var device = await _devices.GetByIdWithUserDevicesAsync(deviceId, ct) ?? throw new InvalidInputException();
        if (!device.IsBlocked && device.InvalidSyncAttemptCount == 0 && device.BlockedReason is null)
            return;

        device.IsBlocked = false;
        device.BlockedReason = null;
        device.BlockedAt = null;
        device.InvalidSyncAttemptCount = 0;
        device.LastInvalidSyncAttemptAt = null;
        device.LastModifiedAt = DateTimeOffset.UtcNow;
        device.GenerateIntegrityHash();
        _devices.Update(device);
        await _syncChanges.EnqueueAsync(new SyncItem { ModelId = device.Id, ModelType = SyncModelType.Device, ChangeType = SyncChangeType.Updated }, ct);
    }

    private Task EnqueueUserDeviceChangeAsync(UserDevice userDevice, SyncChangeType changeType, CancellationToken ct) =>
        _syncChanges.EnqueueAsync(new SyncItem
        {
            ModelId = SyncIdentityUtil.BuildUserDeviceModelId(userDevice.UserId, userDevice.DeviceId),
            ModelType = SyncModelType.UserDevice,
            ChangeType = changeType
        }, ct);

    private async Task RemoveCachedDeviceIfNoPendingAsync(UserDevice userDevice, CancellationToken ct)
    {
        if (userDevice.Device is null || await _syncQueueItems.HasPendingForDeviceAsync(userDevice.DeviceId, ct))
            return;

        if (await _syncRoutes.HasEligibleUserForDeviceAsync(userDevice.DeviceId, ct))
            return;

        _syncDeviceIdentities.TryRemove(userDevice.Device);
    }
}
