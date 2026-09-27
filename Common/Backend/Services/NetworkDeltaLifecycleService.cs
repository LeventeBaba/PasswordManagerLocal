using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models.Projections;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;
using static PasswordManagerLocal.Common.Contracts.Constants.DataLengthConstants;
using System.Security.Cryptography;
using System.Text.Json;
using static PasswordManagerLocal.Common.Backend.Utils.DataCodec;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class NetworkDeltaLifecycleService : INetworkDeltaLifecycleService
{
    private readonly IUserRepository _users;
    private readonly IDeviceRepository _devices;
    private readonly IUserDeviceRepository _userDevices;
    private readonly ISyncTombstoneRepository _tombstones;
    private readonly ISyncQueueRepository _syncQueue;
    private readonly ISyncChangeQueueService _syncChanges;
    private readonly IUserSyncCatchUpService _userSyncCatchUp;
    private readonly ISyncDeviceIdentityService _syncDeviceIdentities;
    private readonly IDeviceIdentityService _identity;
    private readonly ISyncAuthorizationService _authorization;
    private readonly ISyncRuntimeService _syncRuntime;
    private readonly IInteractiveSessionStateService _interactiveSessions;
    private readonly IUnitOfWork _uow;
    private readonly IUserControlOperationRepository? _controlOperations;
    private readonly IUserMembershipAuthorizationRepository? _membershipAuthorizations;

    public NetworkDeltaLifecycleService(
        IUserRepository users,
        IDeviceRepository devices,
        IUserDeviceRepository userDevices,
        ISyncTombstoneRepository tombstones,
        ISyncQueueRepository syncQueue,
        ISyncChangeQueueService syncChanges,
        IUserSyncCatchUpService userSyncCatchUp,
        ISyncDeviceIdentityService syncDeviceIdentities,
        IDeviceIdentityService identity,
        ISyncAuthorizationService authorization,
        ISyncRuntimeService syncRuntime,
        IInteractiveSessionStateService interactiveSessions,
        IUnitOfWork uow,
        IUserControlOperationRepository? controlOperations = null,
        IUserMembershipAuthorizationRepository? membershipAuthorizations = null)
    {
        _users = users;
        _devices = devices;
        _userDevices = userDevices;
        _tombstones = tombstones;
        _syncQueue = syncQueue;
        _syncChanges = syncChanges;
        _userSyncCatchUp = userSyncCatchUp;
        _syncDeviceIdentities = syncDeviceIdentities;
        _identity = identity;
        _authorization = authorization;
        _syncRuntime = syncRuntime;
        _interactiveSessions = interactiveSessions;
        _uow = uow;
        _controlOperations = controlOperations;
        _membershipAuthorizations = membershipAuthorizations;
    }

    public async Task BeforeSaveAsync(
        SyncDeltaPayload payload,
        Device sourceDevice,
        bool applied,
        long ts,
        CancellationToken ct = default)
    {
        if (!SyncPayloadRules.DeletesSourceDevice(payload, sourceDevice) &&
            !SyncPayloadRules.DeletesLocalUserProfile(payload, _identity))
        {
            await TouchSourceDeviceAsync(sourceDevice, ct);
        }
    }

    public async Task AfterSaveAsync(
        SyncDeltaPayload payload,
        Device sourceDevice,
        bool applied,
        long ts,
        CancellationToken ct = default)
    {
        if (SyncPayloadRules.DeletesLocalUserProfile(payload, _identity))
            await _syncRuntime.RefreshSyncEnabledAsync(ct);

        if (applied)
            await RefreshAffectedSessionCachesAsync(payload, ct);

        if (applied && SyncPayloadRules.ShouldPropagate(payload, _identity))
            await PropagateIncomingDeltaAsync(payload, sourceDevice.Id, ts, ct);

        if (applied && SyncPayloadRules.IsRemoteUserDeviceDeletion(payload, _identity))
            await CleanupDetachedDeviceIfUserDeviceDeletionCompletedAsync(payload, ts, ct);

        if (applied &&
            payload.ModelType == SyncModelType.UserDevice &&
            payload.ChangeType != SyncChangeType.Deleted &&
            payload.UserDevice is { IsSyncOn: true, IsDeleted: false } enabledLink &&
            enabledLink.DeviceId != _identity.LocalDeviceId)
        {
            await _userSyncCatchUp.EnqueueAsync(enabledLink.UserId, enabledLink.DeviceId, ct);
        }
    }

    private async Task RefreshAffectedSessionCachesAsync(SyncDeltaPayload payload, CancellationToken ct)
    {
        if (payload.ModelType != SyncModelType.User)
            return;

        var user = await _users.GetByIdWithRelationsAsync(payload.ModelId, ct);
        if (user is null)
            return;

        await _interactiveSessions.RefreshSyncedUserSessionsAsync(user, ct);
    }


    private async Task CleanupDetachedDeviceIfUserDeviceDeletionCompletedAsync(SyncDeltaPayload payload, long ts, CancellationToken ct)
    {
        if (payload.UserDevice is null || payload.UserDevice.DeviceId == _identity.LocalDeviceId)
            return;

        if (await _syncQueue.HasPendingForModelAsync(payload.ModelId, SyncModelType.UserDevice, ct))
            return;

        var deletedUserDevice = await _userDevices.GetByModelIdAsync(payload.ModelId, ct);
        if (deletedUserDevice is not null && !deletedUserDevice.IsDeleted)
            return;

        await _tombstones.UpsertAsync(payload.ModelId, SyncModelType.UserDevice, ts, ct);

        if (!await _userDevices.HasAnyActiveLinkForDeviceAsync(payload.UserDevice.DeviceId, ct))
        {
            var device = await _devices.GetByIdWithUserDevicesAsync(payload.UserDevice.DeviceId, ct);
            if (device is not null)
            {
                _syncDeviceIdentities.TryRemove(device);
                _devices.Delete(device);
            }
        }
        else if (deletedUserDevice is not null)
        {
            _userDevices.Delete(deletedUserDevice);
        }

        await _uow.SaveChangesAsync(ct);
    }


    public async Task TouchSourceDeviceAsync(Device sourceDevice, CancellationToken ct)
    {
        sourceDevice.LastSync = DateTime.UtcNow;
        sourceDevice.LastSeen = DateTime.UtcNow;
        sourceDevice.InvalidSyncAttemptCount = 0;
        sourceDevice.LastInvalidSyncAttemptAt = null;
        sourceDevice.GenerateIntegrityHash();

        await RefreshCachedDeviceAsync(sourceDevice, ct);
    }


    private async Task RefreshCachedDeviceAsync(Device device, CancellationToken ct)
    {
        _syncDeviceIdentities.TryRemove(device);

        if (!device.IsTrusted || device.IsBlocked)
            return;

        var hasOrdinaryWork =
            await _authorization.HasEligibleUserForDeviceAsync(device.Id, ct) &&
            await _syncQueue.HasPendingForDeviceAsync(device.Id, ct);

        if (hasOrdinaryWork || await HasAccountDeletionRelayWorkAsync(device.Id, ct))
            _syncDeviceIdentities.TryAdd(device);
    }

    private async Task<bool> HasAccountDeletionRelayWorkAsync(Guid deviceId, CancellationToken ct)
    {
        if (_controlOperations is null || _membershipAuthorizations is null)
            return false;

        var deletedUserIds = await _controlOperations.ListAppliedAccountDeletionUserIdsAsync(ct);
        if (deletedUserIds.Count == 0)
            return false;

        var historicalUserIds = await _membershipAuthorizations.ListUserIdsForDeviceAsync(deviceId, ct);
        return historicalUserIds.Any(userId => deletedUserIds.Contains(userId));
    }


    private Task PropagateIncomingDeltaAsync(SyncDeltaPayload payload, Guid sourceDeviceId, long changedAtTs, CancellationToken ct) =>
        _syncChanges.EnqueuePropagationAsync(new SyncItem
        {
            ModelId = payload.ModelId,
            ModelType = payload.ModelType,
            ChangeType = payload.ChangeType,
            ChangedAtTs = changedAtTs
        }, sourceDeviceId, changedAtTs, ct);
}
