using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class SyncRouteRepository : ISyncRouteRepository
{
    private readonly DbSet<UserDevice> _userDevices;
    private readonly DbSet<LocalUserDevice> _localUsers;

    public SyncRouteRepository(AppDbContext context)
    {
        _userDevices = context.UserDevices;
        _localUsers = context.LocalUserDevices;
    }

    public Task<bool> IsEligibleAsync(Guid userId, Guid remoteDeviceId, CancellationToken ct = default)
    {
        if (userId == Guid.Empty || remoteDeviceId == Guid.Empty)
            return Task.FromResult(false);

        return EligibleRoutes(remoteDeviceId)
            .AnyAsync(link => link.UserId == userId, ct);
    }

    public Task<bool> HasAnyEligibleAsync(IReadOnlyCollection<Guid> userIds, Guid remoteDeviceId, CancellationToken ct = default)
    {
        var ids = NormalizeUserIds(userIds);
        if (ids.Length == 0 || remoteDeviceId == Guid.Empty)
            return Task.FromResult(false);

        return EligibleRoutes(remoteDeviceId)
            .AnyAsync(link => ids.Contains(link.UserId), ct);
    }

    public async Task<IReadOnlyList<Guid>> ListEligibleUserIdsAsync(
        IReadOnlyCollection<Guid> userIds,
        Guid remoteDeviceId,
        CancellationToken ct = default)
    {
        var ids = NormalizeUserIds(userIds);
        if (ids.Length == 0 || remoteDeviceId == Guid.Empty)
            return [];

        return await EligibleRoutes(remoteDeviceId)
            .Where(link => ids.Contains(link.UserId))
            .Select(link => link.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> ListAllEligibleUserIdsAsync(
        Guid remoteDeviceId,
        CancellationToken ct = default)
    {
        if (remoteDeviceId == Guid.Empty)
            return [];

        return await EligibleRoutes(remoteDeviceId)
            .Select(link => link.UserId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(ct);
    }

    public Task<bool> HasEligibleUserForDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        if (deviceId == Guid.Empty)
            return Task.FromResult(false);

        return EligibleRoutes(deviceId).AnyAsync(ct);
    }

    private IQueryable<UserDevice> EligibleRoutes(Guid remoteDeviceId) =>
        _userDevices.AsNoTracking().Where(link =>
            link.DeviceId == remoteDeviceId &&
            !link.IsDeleted &&
            link.IsSyncOn &&
            _localUsers.Any(local => local.UserId == link.UserId && local.IsSyncOn));

    private Guid[] NormalizeUserIds(IReadOnlyCollection<Guid> userIds) =>
        userIds.Where(id => id != Guid.Empty).Distinct().ToArray();
}
