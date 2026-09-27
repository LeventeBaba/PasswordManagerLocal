using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeLocalUserDeviceRepository : ILocalUserDeviceRepository
{
    private readonly Dictionary<Guid, LocalUserDevice> _items = [];

    internal IReadOnlyCollection<LocalUserDevice> Items => _items.Values;

    public Task<LocalUserDevice?> GetAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue(userId, out var item) ? Clone(item) : null);

    public Task<bool> IsSyncOnAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue(userId, out var item) && item.IsSyncOn);

    public Task<bool> AnySyncOnAsync(CancellationToken ct = default) =>
        Task.FromResult(_items.Values.Any(item => item.IsSyncOn));

    public Task<IReadOnlyList<Guid>> ListSyncOnUserIdsAsync(CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<Guid>)_items.Values
            .Where(item => item.IsSyncOn)
            .Select(item => item.UserId)
            .ToList());

    public Task AddAsync(LocalUserDevice localUserDevice, CancellationToken ct = default)
    {
        _items[localUserDevice.UserId] = Clone(localUserDevice);
        return Task.CompletedTask;
    }

    public void Update(LocalUserDevice localUserDevice) =>
        _items[localUserDevice.UserId] = Clone(localUserDevice);

    public void Delete(LocalUserDevice localUserDevice) =>
        _items.Remove(localUserDevice.UserId);

    private static LocalUserDevice Clone(LocalUserDevice item) =>
        new()
        {
            UserId = item.UserId,
            User = item.User,
            LocalDeviceIdentityId = item.LocalDeviceIdentityId,
            LocalDeviceIdentity = item.LocalDeviceIdentity,
            IsSyncOn = item.IsSyncOn,
            IntegrityHash = item.IntegrityHash.ToArray()
        };
}
