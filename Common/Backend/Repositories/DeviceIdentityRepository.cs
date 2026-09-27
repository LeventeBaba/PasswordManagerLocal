using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

public sealed class DeviceIdentityRepository : IDeviceIdentityRepository
{
    private readonly DbSet<LocalDeviceIdentity> _set;

    public DeviceIdentityRepository(AppDbContext context)
    {
        _set = context.LocalDeviceIdentities;
    }


    public async Task<LocalDeviceIdentity?> Get(CancellationToken ct = default) =>
        await _set.AsNoTracking().FirstOrDefaultAsync(ct);

    public async Task Create(LocalDeviceIdentity identity, CancellationToken ct = default) =>
        await _set.AddAsync(identity, ct);


    public void Update(LocalDeviceIdentity identity) =>
        _set.Update(identity);
}