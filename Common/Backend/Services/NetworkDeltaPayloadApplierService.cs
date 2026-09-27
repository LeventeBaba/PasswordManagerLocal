using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models.Projections;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using static PasswordManagerLocal.Common.Contracts.Constants.DataLengthConstants;
using System.Security.Cryptography;
using System.Text.Json;
using static PasswordManagerLocal.Common.Backend.Utils.DataCodec;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class NetworkDeltaPayloadApplierService : INetworkDeltaPayloadApplierService
{
    private readonly IUserDeltaApplierService _userDeltas;
    private readonly IUserRepository _users;
    private readonly IGroupRepository _groups;
    private readonly IDeviceRepository _devices;
    private readonly IUserDeviceRepository _userDevices;
    private readonly ISyncRouteRepository _syncRoutes;
    private readonly ISyncTombstoneRepository _tombstones;
    private readonly ISyncQueueRepository _syncQueue;
    private readonly ISyncDeviceIdentityService _syncDeviceIdentities;
    private readonly IDeviceIdentityService _identity;
    private readonly ISyncAuthorizationService _authorization;
    private readonly IInteractiveSessionStateService _interactiveSessions;
    private readonly ISyncRelationshipReconciliationService _relationships;
    private readonly IUserMembershipAuthorizationRepository _membershipAuthorizations;

    public NetworkDeltaPayloadApplierService(
        IUserDeltaApplierService userDeltas,
        IUserRepository users,
        IGroupRepository groups,
        IDeviceRepository devices,
        IUserDeviceRepository userDevices,
        ISyncRouteRepository syncRoutes,
        ISyncTombstoneRepository tombstones,
        ISyncQueueRepository syncQueue,
        ISyncDeviceIdentityService syncDeviceIdentities,
        IDeviceIdentityService identity,
        ISyncAuthorizationService authorization,
        IInteractiveSessionStateService interactiveSessions,
        ISyncRelationshipReconciliationService relationships,
        IUserMembershipAuthorizationRepository membershipAuthorizations)
    {
        _userDeltas = userDeltas;
        _users = users;
        _groups = groups;
        _devices = devices;
        _userDevices = userDevices;
        _syncRoutes = syncRoutes;
        _tombstones = tombstones;
        _syncQueue = syncQueue;
        _syncDeviceIdentities = syncDeviceIdentities;
        _identity = identity;
        _authorization = authorization;
        _interactiveSessions = interactiveSessions;
        _relationships = relationships;
        _membershipAuthorizations = membershipAuthorizations;
    }

    public Task<bool> ApplyAsync(SyncDeltaPayload payload, Guid sourceDeviceId, long ts, CancellationToken ct = default) =>
        payload.ModelType switch
        {
            SyncModelType.User => _userDeltas.ApplyAsync(payload, sourceDeviceId, ts, ct),
            SyncModelType.Group => ApplyGroupAsync(payload, ts, ct),
            SyncModelType.Device => ApplyDeviceAsync(payload, ts, ct),
            SyncModelType.UserDevice => ApplyUserDeviceAsync(payload, ts, ct),
            _ => throw new InvalidOperationException("Unknown sync model type.")
        };

    private async Task<bool> ApplyGroupAsync(SyncDeltaPayload delta, long ts, CancellationToken ct)
    {
        var existing = await _groups.GetByIdWithUsersAsync(delta.ModelId, ct);

        if (existing is not null && IsIncomingOlderOrSame(existing.LastModifiedAt, ts))
            return false;

        if (delta.ChangeType == SyncChangeType.Deleted)
        {
            if (existing is not null)
                _groups.Delete(existing);

            await _tombstones.UpsertAsync(delta.ModelId, delta.ModelType, ts, ct);
            return true;
        }

        if (delta.Group is null)
            throw new InvalidDataException("Group sync payload is missing.");

        var group = existing ?? CreateGroup(delta.Group);
        CopyGroupData(delta.Group, group);
        group.LastModifiedAt = FromTimestamp(ts);

        if (existing is null)
            await _groups.AddAsync(group, ct);

        await _relationships.SyncGroupUsersAsync(group, delta.Group.UserIds, ct);
        group.GenerateIntegrityHash();
        await RemoveTombstoneAsync(delta, ct);
        return true;
    }


    private async Task<bool> ApplyDeviceAsync(SyncDeltaPayload delta, long ts, CancellationToken ct)
    {
        if (SyncPayloadRules.IsLocalDevicePayload(delta, _identity))
            return false;

        var existing = await _devices.GetByIdWithUserDevicesAsync(delta.ModelId, ct);

        if (existing is not null && IsIncomingOlderOrSame(existing.LastModifiedAt, ts))
            return false;

        if (delta.ChangeType == SyncChangeType.Deleted)
        {
            // Mutable device deltas are never membership authority. Keep a current trusted identity
            // while any present relational membership remains active; signed removal operations end it.
            if (existing is not null && existing.UserDevices.Any(link => !link.IsDeleted))
                return false;
            if (existing is not null)
            {
                _syncDeviceIdentities.TryRemove(existing);
                _devices.Delete(existing);
            }

            await _tombstones.UpsertAsync(delta.ModelId, delta.ModelType, ts, ct);
            return true;
        }

        if (delta.Device is null)
            throw new InvalidDataException("Device sync payload is missing.");

        var isNew = existing is null;
        var device = existing ?? CreateDevice(delta.Device);
        CopyDeviceData(delta.Device, device, isNew);
        device.LastModifiedAt = FromTimestamp(ts);

        if (isNew)
            await _devices.AddAsync(device, ct);

        await _relationships.SyncDeviceUsersAsync(device, delta.Device.UserIds, device.LastModifiedAt, ct);
        device.GenerateIntegrityHash();
        await RemoveTombstoneAsync(delta, ct);

        await RefreshCachedDeviceAsync(device, ct);
        return true;
    }


    private async Task<bool> ApplyUserDeviceAsync(SyncDeltaPayload delta, long ts, CancellationToken ct)
    {
        if (delta.UserDevice is null)
            throw new InvalidDataException("User device sync payload is missing.");

        SyncPayloadRules.ValidateUserDevicePayload(delta);

        if (SyncPayloadRules.DeletesLocalUserProfile(delta, _identity))
            return await ApplyLocalUserProfileDisconnectAsync(delta, ts, ct);

        var payload = delta.UserDevice;
        var activeAuthorizations = await _membershipAuthorizations.ListActiveForDeviceAsync(payload.UserId, payload.DeviceId, ct);
        var hasAuthoritativeMembership = activeAuthorizations.Count != 0;
        var existing = await _userDevices.GetAsync(payload.UserId, payload.DeviceId, ct);
        existing?.VerifyIntegrity();
        if (existing is not null && IsIncomingOlderOrSame(existing.LastModifiedAt, ts))
            return false;

        var modifiedAt = FromTimestamp(ts);
        if (delta.ChangeType == SyncChangeType.Deleted || payload.IsDeleted)
        {
            if (hasAuthoritativeMembership)
                return false;
            if (existing is not null)
            {
                existing.IsDeleted = true;
                existing.IsSyncOn = false;
                existing.DeletedAt = UtcDateTimeUtil.ToUtc(payload.DeletedAt ?? modifiedAt);
                existing.LastModifiedAt = modifiedAt;
                existing.GenerateIntegrityHash();
                _userDevices.Update(existing);
            }

            await _tombstones.UpsertAsync(delta.ModelId, delta.ModelType, ts, ct);
            if (payload.DeviceId != _identity.LocalDeviceId)
                await RemovePendingSyncsForUserToDeviceAsync(payload.UserId, payload.DeviceId, ct);
            return true;
        }

        if (!hasAuthoritativeMembership)
            return false;

        var remoteDevice = await _devices.GetByIdAsync(payload.DeviceId, ct);
        if (remoteDevice is null)
            throw new InvalidDataException("Remote device was not found for the user-device setting.");

        var userDevice = existing ?? new UserDevice
        {
            UserId = payload.UserId,
            DeviceId = payload.DeviceId
        };

        userDevice.Device = remoteDevice;
        userDevice.IsDeleted = false;
        userDevice.IsSyncOn = payload.IsSyncOn;
        userDevice.DeletedAt = null;
        userDevice.LastModifiedAt = modifiedAt;
        userDevice.GenerateIntegrityHash();

        if (existing is null)
            await _userDevices.AddAsync(userDevice, ct);
        else
            _userDevices.Update(userDevice);

        await RemoveTombstoneAsync(delta, ct);
        return true;
    }


    private async Task RemovePendingSyncsForUserToDeviceAsync(Guid userId, Guid targetDeviceId, CancellationToken ct)
    {
        var pendingItems = await _syncQueue.ListPendingForDeviceWithItemsAsync(targetDeviceId, ct);
        foreach (var queueItem in pendingItems)
        {
            if (queueItem.SyncItem is not null && await IsSyncItemOnlyForRemovedUserOrRouteAsync(queueItem.SyncItem, userId, targetDeviceId, ct))
                _syncQueue.Delete(queueItem);
        }
    }


    private async Task<bool> IsSyncItemOnlyForRemovedUserOrRouteAsync(SyncItem item, Guid removedUserId, Guid targetDeviceId, CancellationToken ct)
    {
        if (item.ModelType == SyncModelType.User)
            return item.ModelId == removedUserId;

        if (item.ModelType == SyncModelType.UserDevice)
        {
            var link = await _userDevices.GetByModelIdAsync(item.ModelId, ct);
            return link?.UserId == removedUserId;
        }

        if (item.ModelType == SyncModelType.Group)
        {
            var userIds = await _groups.ListUserIdsAsync(item.ModelId, ct);
            if (!userIds.Contains(removedUserId))
                return false;

            return !await AnyOtherUserCanStillSyncToTargetAsync(userIds, removedUserId, targetDeviceId, ct);
        }

        if (item.ModelType == SyncModelType.Device)
        {
            var links = await _userDevices.ListByDeviceAsync(item.ModelId, ct);
            if (links.All(link => link.UserId != removedUserId))
                return false;

            return !await AnyOtherUserCanStillSyncToTargetAsync(
                links.Where(link => !link.IsDeleted && link.IsSyncOn).Select(link => link.UserId),
                removedUserId,
                targetDeviceId,
                ct);
        }

        return false;
    }


    private Task<bool> AnyOtherUserCanStillSyncToTargetAsync(
        IEnumerable<Guid> userIds,
        Guid removedUserId,
        Guid targetDeviceId,
        CancellationToken ct)
    {
        var candidateUserIds = userIds
            .Where(id => id != Guid.Empty && id != removedUserId)
            .Distinct()
            .ToArray();
        return _syncRoutes.HasAnyEligibleAsync(candidateUserIds, targetDeviceId, ct);
    }


    private async Task<bool> ApplyLocalUserProfileDisconnectAsync(SyncDeltaPayload delta, long ts, CancellationToken ct)
    {
        if (delta.UserDevice is null)
            throw new InvalidDataException("User device sync payload is missing.");

        await _tombstones.UpsertAsync(delta.ModelId, delta.ModelType, ts, ct);

        var user = await _users.GetByIdWithRelationsAsync(delta.UserDevice.UserId, ct);
        if (user is null)
            return false;

        var relatedDeviceIds = user.UserDevices
            .Where(ud => ud.DeviceId != Guid.Empty && ud.DeviceId != _identity.LocalDeviceId)
            .Select(ud => ud.DeviceId)
            .Distinct()
            .ToList();

        foreach (var deviceId in relatedDeviceIds)
            await RemovePendingSyncsForUserToDeviceAsync(user.UId, deviceId, ct);

        await _interactiveSessions.LogoutUserAsync(
            user.UId,
            AuthSessionInvalidationReason.ProfileRemoved,
            CancellationToken.None);
        _users.Delete(user);

        foreach (var deviceId in relatedDeviceIds)
        {
            if (await _userDevices.HasAnyActiveLinkForDeviceExceptUserAsync(deviceId, user.UId, ct))
                continue;

            var device = await _devices.GetByIdWithUserDevicesAsync(deviceId, ct);
            if (device is null)
                continue;

            _syncDeviceIdentities.TryRemove(device);
            _devices.Delete(device);
        }

        return true;
    }


    private async Task RefreshCachedDeviceAsync(Device device, CancellationToken ct)
    {
        _syncDeviceIdentities.TryRemove(device);

        if (!device.IsTrusted || device.IsBlocked)
            return;

        if (!await _authorization.HasEligibleUserForDeviceAsync(device.Id, ct))
            return;

        if (await _syncQueue.HasPendingForDeviceAsync(device.Id, ct))
            _syncDeviceIdentities.TryAdd(device);
    }


    private async Task RemoveTombstoneAsync(SyncDeltaPayload payload, CancellationToken ct)
    {
        var tombstone = await _tombstones.GetAsync(payload.ModelId, payload.ModelType, ct);
        if (tombstone is not null)
            _tombstones.Delete(tombstone);
    }


    private Group CreateGroup(GroupSyncPayload payload) =>
        new()
        {
            Id = payload.Id
        };


    private Device CreateDevice(DeviceSyncPayload payload) =>
        new()
        {
            Id = payload.Id
        };


    private void CopyGroupData(GroupSyncPayload source, Group target)
    {
        target.Id = source.Id;
        target.EncryptedPayload = source.EncryptedPayload;
        target.IntegrityHash = source.IntegrityHash;
    }


    private void CopyDeviceData(DeviceSyncPayload source, Device target, bool isNew)
    {
        target.Id = source.Id;
        target.PublicKey = source.PublicKey;
        target.SignPublicKey = source.SignPublicKey;
        target.TlsCertFingerprint = source.TlsCertFingerprint;
        target.DeviceType = source.DeviceType;
        target.LastKnownHash = source.LastKnownHash;
        target.IntegrityHash = source.IntegrityHash;

        if (isNew)
        {
            target.LastSync = UtcDateTimeUtil.ToUtc(source.LastSync);
            target.LastSeen = UtcDateTimeUtil.ToUtc(source.LastSeen);
            target.IsTrusted = source.IsTrusted;
            target.IsBlocked = source.IsBlocked;
            target.BlockedReason = source.BlockedReason;
            target.BlockedAt = UtcDateTimeUtil.ToUtc(source.BlockedAt);
            target.InvalidSyncAttemptCount = source.InvalidSyncAttemptCount;
            target.LastInvalidSyncAttemptAt = UtcDateTimeUtil.ToUtc(source.LastInvalidSyncAttemptAt);
            return;
        }

        if (source.IsBlocked)
        {
            target.IsBlocked = true;
            target.BlockedReason = source.BlockedReason;
            target.BlockedAt = UtcDateTimeUtil.ToUtc(source.BlockedAt ?? DateTimeOffset.UtcNow);
        }
    }


    private DateTimeOffset FromTimestamp(long ts) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ts);


    private bool IsIncomingOlderOrSame(DateTimeOffset local, long incomingTs) =>
        local.ToUnixTimeMilliseconds() >= incomingTs;
}
