using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class LocalUserDeviceRepository : ILocalUserDeviceRepository
{
    private readonly DbSet<LocalUserDevice> _set;

    public LocalUserDeviceRepository(AppDbContext context)
    {
        _set = context.LocalUserDevices;
    }

    public Task<LocalUserDevice?> GetAsync(Guid userId, CancellationToken ct = default) =>
        _set.Include(x => x.LocalDeviceIdentity).FirstOrDefaultAsync(x => x.UserId == userId, ct);

    public Task<bool> IsSyncOnAsync(Guid userId, CancellationToken ct = default) =>
        _set.AsNoTracking().AnyAsync(x => x.UserId == userId && x.IsSyncOn, ct);

    public Task<bool> AnySyncOnAsync(CancellationToken ct = default) =>
        _set.AsNoTracking().AnyAsync(x => x.IsSyncOn, ct);

    public async Task<IReadOnlyList<Guid>> ListSyncOnUserIdsAsync(CancellationToken ct = default) =>
        await _set.AsNoTracking()
            .Where(x => x.IsSyncOn)
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(ct);

    public Task AddAsync(LocalUserDevice localUserDevice, CancellationToken ct = default) =>
        _set.AddAsync(localUserDevice, ct).AsTask();

    public void Update(LocalUserDevice localUserDevice) =>
        _set.Update(localUserDevice);

    public void Delete(LocalUserDevice localUserDevice) =>
        _set.Remove(localUserDevice);
}
