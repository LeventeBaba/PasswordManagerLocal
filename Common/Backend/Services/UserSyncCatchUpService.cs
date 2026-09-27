using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Builds the complete user relationship graph required to catch up one target device.
/// </summary>
public sealed class UserSyncCatchUpService : IUserSyncCatchUpService
{
    private readonly IUserRepository _users;
    private readonly IDeviceRepository _devices;
    private readonly ISyncQueueWriterService _queue;

    public UserSyncCatchUpService(
        IUserRepository users,
        IDeviceRepository devices,
        ISyncQueueWriterService queue)
    {
        _users = users;
        _devices = devices;
        _queue = queue;
    }

    public async Task EnqueueAsync(Guid userId, Guid targetDeviceId, CancellationToken ct = default)
    {
        var user = await _users.GetByIdWithRelationsAsync(userId, ct);
        if (user is null)
            return;

        await _queue.EnqueueForDeviceAsync(new SyncItem
        {
            ModelId = userId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated,
            ChangedAtTs = ToSyncTimestamp(user.LastModifiedAt)
        }, targetDeviceId, ct);

        foreach (var group in user.Groups)
        {
            await _queue.EnqueueForDeviceAsync(new SyncItem
            {
                ModelId = group.Id,
                ModelType = SyncModelType.Group,
                ChangeType = SyncChangeType.Updated,
                ChangedAtTs = ToSyncTimestamp(group.LastModifiedAt)
            }, targetDeviceId, ct);
        }

        var sourceDeviceIds = user.UserDevices
            .Where(link => !link.IsDeleted && link.DeviceId != targetDeviceId)
            .Select(link => link.DeviceId)
            .Distinct()
            .ToArray();
        var sourceDevices = (await _devices.ListByIdsAsync(sourceDeviceIds, ct))
            .ToDictionary(device => device.Id);

        foreach (var link in user.UserDevices)
        {
            if (!link.IsDeleted &&
                link.DeviceId != targetDeviceId &&
                sourceDevices.TryGetValue(link.DeviceId, out var sourceDevice))
            {
                await _queue.EnqueueForDeviceAsync(new SyncItem
                {
                    ModelId = link.DeviceId,
                    ModelType = SyncModelType.Device,
                    ChangeType = SyncChangeType.Updated,
                    ChangedAtTs = ToSyncTimestamp(sourceDevice.LastModifiedAt)
                }, targetDeviceId, ct);
            }

            await _queue.EnqueueForDeviceAsync(new SyncItem
            {
                ModelId = SyncIdentityUtil.BuildUserDeviceModelId(link.UserId, link.DeviceId),
                ModelType = SyncModelType.UserDevice,
                ChangeType = link.IsDeleted ? SyncChangeType.Deleted : SyncChangeType.Updated,
                ChangedAtTs = ToSyncTimestamp(link.LastModifiedAt)
            }, targetDeviceId, ct);
        }
    }

    private long ToSyncTimestamp(DateTimeOffset modifiedAt) =>
        (modifiedAt == default ? DateTimeOffset.UtcNow : modifiedAt).ToUnixTimeMilliseconds();
}
