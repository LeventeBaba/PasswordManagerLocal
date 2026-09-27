using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Owns synchronization-item coalescing, local model timestamps, and non-user tombstones.
/// </summary>
public sealed class SyncItemLifecycleService : ISyncItemLifecycleService
{
    private readonly ISyncItemRepository _syncItems;
    private readonly ISyncQueueRepository _syncQueue;
    private readonly IUserRepository _users;
    private readonly IGroupRepository _groups;
    private readonly IDeviceRepository _devices;
    private readonly IUserDeviceRepository _userDevices;
    private readonly ISyncTombstoneRepository _tombstones;
    private readonly ILocalDeviceMatcherService _localDevices;
    private readonly IUserCanonicalHealthService? _canonicalHealth;

    public SyncItemLifecycleService(
        ISyncItemRepository syncItems,
        ISyncQueueRepository syncQueue,
        IUserRepository users,
        IGroupRepository groups,
        IDeviceRepository devices,
        IUserDeviceRepository userDevices,
        ISyncTombstoneRepository tombstones,
        ILocalDeviceMatcherService localDevices,
        IUserCanonicalHealthService? canonicalHealth = null)
    {
        _syncItems = syncItems;
        _syncQueue = syncQueue;
        _users = users;
        _groups = groups;
        _devices = devices;
        _userDevices = userDevices;
        _tombstones = tombstones;
        _localDevices = localDevices;
        _canonicalHealth = canonicalHealth;
    }

    public async Task<SyncItem> GetOrCreateAsync(SyncItem item, long changedAtTs, CancellationToken ct = default)
    {
        if (item.ModelType == SyncModelType.User && item.ChangeType == SyncChangeType.Deleted)
            throw new InvalidOperationException("Generic user-deletion queue items are disabled; use the signed AccountDeletion control operation.");

        var existing = await _syncItems.GetAsync(item.ModelId, item.ModelType, ct);
        if (existing is not null)
        {
            if (existing.ChangedAtTs > changedAtTs)
                return existing;

            if (await _syncQueue.HasPendingForSyncItemAsync(existing.Id, ct))
                existing.ChangeType = MergeChangeType(existing.ChangeType, item.ChangeType);
            else
                existing.ChangeType = item.ChangeType;

            existing.ChangedAtTs = changedAtTs;
            _syncItems.Update(existing);
            return existing;
        }

        item.ChangedAtTs = changedAtTs;
        await _syncItems.AddAsync(item, ct);
        return item;
    }

    public async Task TouchLocalStateAsync(SyncItem item, long changedAtTs, CancellationToken ct = default)
    {
        if (item.ModelType == SyncModelType.User && item.ChangeType == SyncChangeType.Deleted)
            throw new InvalidOperationException("Generic user-deletion lifecycle updates are disabled; use the signed AccountDeletion control operation.");

        if (item.ChangeType == SyncChangeType.Deleted &&
            item.ModelType is SyncModelType.Group or SyncModelType.Device)
        {
            await _tombstones.UpsertAsync(item.ModelId, item.ModelType, changedAtTs, ct);
            return;
        }

        var modifiedAt = DateTimeOffset.FromUnixTimeMilliseconds(changedAtTs);

        if (item.ModelType == SyncModelType.User)
        {
            var user = await _users.GetByIdWithRelationsAsync(item.ModelId, ct);
            if (user is null)
                return;

            // A queue/lifecycle timestamp touch must never turn an externally corrupted row into
            // a newly signed checkpoint while the user key is unavailable. Verify the existing
            // row and checkpoint before changing any synchronized canonical field.
            if (_canonicalHealth is not null)
            {
                var baseline = await _canonicalHealth.VerifyAsync(
                    user,
                    key: null,
                    keyConfidence: UserSyncKeyConfidence.UnconfirmedPassword,
                    recordFault: true,
                    ct: ct);
                if (!baseline.IsPublishable)
                {
                    throw new InvalidDataException(
                        "Local canonical user data is not healthy enough for a synchronization lifecycle update.");
                }
            }

            user.LastModifiedAt = modifiedAt;
            user.GenerateIntegrityHash();
            _users.Update(user);
            if (_canonicalHealth is not null)
                await _canonicalHealth.UpdateCheckpointAsync(user, ct);
            await RemoveTombstoneAsync(item, ct);
            return;
        }

        if (item.ModelType == SyncModelType.Group)
        {
            var group = await _groups.GetByIdAsync(item.ModelId, ct);
            if (group is null)
                return;

            group.LastModifiedAt = modifiedAt;
            group.GenerateIntegrityHash();
            _groups.Update(group);
            await RemoveTombstoneAsync(item, ct);
            return;
        }

        if (item.ModelType == SyncModelType.Device)
        {
            var device = await _devices.GetByIdWithUserDevicesAsync(item.ModelId, ct);
            if (device is null || _localDevices.IsLocalDevice(device))
                return;

            device.LastModifiedAt = modifiedAt;
            device.GenerateIntegrityHash();
            _devices.Update(device);
            await RemoveTombstoneAsync(item, ct);
            return;
        }

        if (item.ModelType == SyncModelType.UserDevice)
        {
            var userDevice = await _userDevices.GetByModelIdAsync(item.ModelId, ct);
            if (userDevice is null)
                return;

            userDevice.VerifyIntegrity();
            userDevice.LastModifiedAt = modifiedAt;
            userDevice.GenerateIntegrityHash();
            _userDevices.Update(userDevice);
        }
    }

    private async Task RemoveTombstoneAsync(SyncItem item, CancellationToken ct)
    {
        var tombstone = await _tombstones.GetAsync(item.ModelId, item.ModelType, ct);
        if (tombstone is not null)
            _tombstones.Delete(tombstone);
    }

    private SyncChangeType MergeChangeType(SyncChangeType current, SyncChangeType incoming)
    {
        if (current == incoming)
            return current;

        if (current == SyncChangeType.Created && incoming == SyncChangeType.Updated)
            return SyncChangeType.Created;

        if (incoming == SyncChangeType.Deleted)
            return SyncChangeType.Deleted;

        if (current == SyncChangeType.Deleted)
            return SyncChangeType.Deleted;

        return incoming;
    }
}
