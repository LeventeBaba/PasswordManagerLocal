using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Internal.Devices;

public sealed class LocalUserDeviceLinkManager
{
    private readonly ILocalUserDeviceRepository _localUserDevices;
    private readonly IDeviceIdentityService _identity;
    private readonly IUnitOfWork _uow;
    private readonly ISyncRuntimeService _syncRuntime;

    public LocalUserDeviceLinkManager(
        ILocalUserDeviceRepository localUserDevices,
        IDeviceIdentityService identity,
        IUnitOfWork uow,
        ISyncRuntimeService syncRuntime)
    {
        _localUserDevices = localUserDevices;
        _identity = identity;
        _uow = uow;
        _syncRuntime = syncRuntime;
    }

    public async Task<LocalUserDevice> GetOrCreateAsync(Guid userId, CancellationToken ct)
    {
        var link = await _localUserDevices.GetAsync(userId, ct);
        if (link is not null)
            return link;
        link = new LocalUserDevice
        {
            UserId = userId,
            LocalDeviceIdentityId = _identity.LocalDeviceId,
            IsSyncOn = true
        };
        link.GenerateIntegrityHash();
        await _localUserDevices.AddAsync(link, ct);
        await _uow.SaveChangesAsync(ct);
        await _syncRuntime.RefreshSyncEnabledAsync(ct);
        return link;
    }
}
