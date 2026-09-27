using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeUserDeviceRepository : IUserDeviceRepository
{
    private readonly List<UserDevice> _items = [];

    internal IReadOnlyList<UserDevice> Items => _items;

    public Task<IReadOnlyList<UserDevice>> ListByUserAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<UserDevice>)_items
            .Where(item => item.UserId == userId)
            .Select(item => Clone(item, includeDevice: false))
            .ToList());

    public Task<IReadOnlyList<UserDevice>> ListByUserWithDevicesAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<UserDevice>)_items
            .Where(item => item.UserId == userId)
            .Select(item => Clone(item, includeDevice: true))
            .ToList());

    public Task<IReadOnlyList<UserDevice>> ListByUsersWithDevicesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<UserDevice>)_items
            .Where(item => userIds.Contains(item.UserId))
            .Select(item => Clone(item, includeDevice: true))
            .ToList());

    public Task<IReadOnlyList<UserDevice>> ListByDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<UserDevice>)_items
            .Where(item => item.DeviceId == deviceId)
            .Select(item => Clone(item, includeDevice: false))
            .ToList());

    public Task<IReadOnlyList<UserDevice>> ListByUserIdsAndDeviceAsync(
        IReadOnlyCollection<Guid> userIds,
        Guid deviceId,
        CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<UserDevice>)_items
            .Where(item => userIds.Contains(item.UserId) && item.DeviceId == deviceId)
            .Select(item => Clone(item, includeDevice: false))
            .ToList());

    public Task<UserDevice?> GetAsync(Guid userId, Guid deviceId, CancellationToken ct = default) =>
        Task.FromResult(_items
            .Where(item => item.UserId == userId && item.DeviceId == deviceId)
            .Select(item => Clone(item, includeDevice: false))
            .FirstOrDefault());

    public Task<UserDevice?> GetWithDeviceAsync(Guid userId, Guid deviceId, CancellationToken ct = default) =>
        Task.FromResult(_items
            .Where(item => item.UserId == userId && item.DeviceId == deviceId)
            .Select(item => Clone(item, includeDevice: true))
            .FirstOrDefault());

    public Task<UserDevice?> GetByModelIdAsync(Guid modelId, CancellationToken ct = default)
    {
        var result = _items
            .Where(item => PasswordManagerLocal.Common.Backend.Sync.SyncIdentityUtil.BuildUserDeviceModelId(item.UserId, item.DeviceId) == modelId)
            .Select(item => Clone(item, includeDevice: false))
            .FirstOrDefault();

        return Task.FromResult(result);
    }

    public Task<bool> ExistsAsync(Guid userId, Guid deviceId, CancellationToken ct = default) =>
        Task.FromResult(_items.Any(item => item.UserId == userId && item.DeviceId == deviceId));

    public Task<bool> HasActiveLinkAsync(Guid userId, Guid deviceId, CancellationToken ct = default) =>
        Task.FromResult(_items.Any(item =>
            item.UserId == userId &&
            item.DeviceId == deviceId &&
            !item.IsDeleted &&
            item.IsSyncOn));

    public Task<bool> HasAnyActiveLinkAsync(IReadOnlyCollection<Guid> userIds, Guid deviceId, CancellationToken ct = default) =>
        Task.FromResult(_items.Any(item =>
            userIds.Contains(item.UserId) &&
            item.DeviceId == deviceId &&
            !item.IsDeleted &&
            item.IsSyncOn));

    public Task<bool> HasAnyActiveLinkForDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        Task.FromResult(_items.Any(item => item.DeviceId == deviceId && !item.IsDeleted));

    public Task<bool> HasAnyActiveSyncEnabledLinkForDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        Task.FromResult(_items.Any(item => item.DeviceId == deviceId && !item.IsDeleted && item.IsSyncOn));

    public Task<bool> HasAnyDeletedLinkForDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        Task.FromResult(_items.Any(item => item.DeviceId == deviceId && item.IsDeleted));

    public Task<bool> HasAnyActiveLinkForDeviceExceptUserAsync(Guid deviceId, Guid userId, CancellationToken ct = default) =>
        Task.FromResult(_items.Any(item => item.DeviceId == deviceId && item.UserId != userId && !item.IsDeleted));

    public Task<bool> SharesActiveUserAsync(Guid sourceDeviceId, Guid targetDeviceId, CancellationToken ct = default)
    {
        var sourceUsers = _items
            .Where(item => item.DeviceId == sourceDeviceId && !item.IsDeleted && item.IsSyncOn)
            .Select(item => item.UserId)
            .ToHashSet();

        return Task.FromResult(_items.Any(item =>
            item.DeviceId == targetDeviceId &&
            !item.IsDeleted &&
            sourceUsers.Contains(item.UserId)));
    }

    public Task AddAsync(UserDevice userDevice, CancellationToken ct = default)
    {
        _items.RemoveAll(item => item.UserId == userDevice.UserId && item.DeviceId == userDevice.DeviceId);
        _items.Add(Clone(userDevice, includeDevice: true));
        return Task.CompletedTask;
    }

    public void Update(UserDevice userDevice)
    {
        _items.RemoveAll(item => item.UserId == userDevice.UserId && item.DeviceId == userDevice.DeviceId);
        _items.Add(Clone(userDevice, includeDevice: true));
    }

    public void Delete(UserDevice userDevice) =>
        _items.RemoveAll(item => item.UserId == userDevice.UserId && item.DeviceId == userDevice.DeviceId);

    private static UserDevice Clone(UserDevice item, bool includeDevice) =>
        new()
        {
            ModelId = item.ModelId,
            UserId = item.UserId,
            DeviceId = item.DeviceId,
            Device = includeDevice ? item.Device : null,
            IsSyncOn = item.IsSyncOn,
            IsDeleted = item.IsDeleted,
            DeletedAt = item.DeletedAt,
            LastModifiedAt = item.LastModifiedAt,
            IntegrityHash = item.IntegrityHash.ToArray()
        };
}
