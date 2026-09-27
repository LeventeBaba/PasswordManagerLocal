using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class UserDeviceRepository : IUserDeviceRepository
{
    private readonly DbSet<UserDevice> _set;

    public UserDeviceRepository(AppDbContext context)
    {
        _set = context.UserDevices;
    }

    public async Task<IReadOnlyList<UserDevice>> ListByUserAsync(Guid userId, CancellationToken ct = default) =>
        await _set.AsNoTracking()
            .Where(link => link.UserId == userId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<UserDevice>> ListByUserWithDevicesAsync(Guid userId, CancellationToken ct = default) =>
        await _set.AsNoTracking()
            .Include(link => link.Device)
            .Where(link => link.UserId == userId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<UserDevice>> ListByUsersWithDevicesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return [];

        return await _set.AsNoTracking()
            .Include(link => link.Device)
            .Where(link => userIds.Contains(link.UserId))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<UserDevice>> ListByDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        await _set.AsNoTracking()
            .Where(link => link.DeviceId == deviceId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<UserDevice>> ListByUserIdsAndDeviceAsync(
        IReadOnlyCollection<Guid> userIds,
        Guid deviceId,
        CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return [];

        return await _set
            .Where(link => link.DeviceId == deviceId && userIds.Contains(link.UserId))
            .ToListAsync(ct);
    }

    public Task<UserDevice?> GetAsync(Guid userId, Guid deviceId, CancellationToken ct = default) =>
        _set.FirstOrDefaultAsync(link => link.UserId == userId && link.DeviceId == deviceId, ct);

    public Task<UserDevice?> GetWithDeviceAsync(Guid userId, Guid deviceId, CancellationToken ct = default) =>
        _set.Include(link => link.Device)
            .FirstOrDefaultAsync(link => link.UserId == userId && link.DeviceId == deviceId, ct);

    public Task<UserDevice?> GetByModelIdAsync(Guid modelId, CancellationToken ct = default) =>
        _set.FirstOrDefaultAsync(link => link.ModelId == modelId, ct);

    public Task<bool> ExistsAsync(Guid userId, Guid deviceId, CancellationToken ct = default) =>
        _set.AsNoTracking().AnyAsync(link => link.UserId == userId && link.DeviceId == deviceId, ct);

    public Task<bool> HasActiveLinkAsync(Guid userId, Guid deviceId, CancellationToken ct = default) =>
        _set.AsNoTracking().AnyAsync(link =>
            link.UserId == userId &&
            link.DeviceId == deviceId &&
            !link.IsDeleted &&
            link.IsSyncOn, ct);

    public Task<bool> HasAnyActiveLinkAsync(IReadOnlyCollection<Guid> userIds, Guid deviceId, CancellationToken ct = default)
    {
        if (userIds.Count == 0 || deviceId == Guid.Empty)
            return Task.FromResult(false);

        return _set.AsNoTracking().AnyAsync(link =>
            userIds.Contains(link.UserId) &&
            link.DeviceId == deviceId &&
            !link.IsDeleted &&
            link.IsSyncOn, ct);
    }

    public Task<bool> HasAnyActiveLinkForDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        _set.AsNoTracking().AnyAsync(link => link.DeviceId == deviceId && !link.IsDeleted, ct);

    public Task<bool> HasAnyActiveSyncEnabledLinkForDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        _set.AsNoTracking().AnyAsync(link => link.DeviceId == deviceId && !link.IsDeleted && link.IsSyncOn, ct);

    public Task<bool> HasAnyDeletedLinkForDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        _set.AsNoTracking().AnyAsync(link => link.DeviceId == deviceId && link.IsDeleted, ct);

    public Task<bool> HasAnyActiveLinkForDeviceExceptUserAsync(Guid deviceId, Guid userId, CancellationToken ct = default) =>
        _set.AsNoTracking().AnyAsync(link => link.DeviceId == deviceId && link.UserId != userId && !link.IsDeleted, ct);

    public Task<bool> SharesActiveUserAsync(Guid sourceDeviceId, Guid targetDeviceId, CancellationToken ct = default) =>
        _set.AsNoTracking().AnyAsync(target =>
            target.DeviceId == targetDeviceId && !target.IsDeleted &&
            _set.Any(source =>
                source.DeviceId == sourceDeviceId &&
                source.UserId == target.UserId &&
                !source.IsDeleted &&
                source.IsSyncOn), ct);

    public Task AddAsync(UserDevice userDevice, CancellationToken ct = default) =>
        _set.AddAsync(userDevice, ct).AsTask();

    public void Update(UserDevice userDevice) =>
        _set.Update(userDevice);

    public void Delete(UserDevice userDevice) =>
        _set.Remove(userDevice);
}
