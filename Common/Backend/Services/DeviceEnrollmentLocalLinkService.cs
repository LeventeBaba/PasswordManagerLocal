using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class DeviceEnrollmentLocalLinkService : IDeviceEnrollmentLocalLinkService
{
    private readonly IDeviceIdentityService _identity;

    public DeviceEnrollmentLocalLinkService(IDeviceIdentityService identity)
    {
        _identity = identity;
    }

    public async Task RemoveLocalDeviceRowsAsync(IDeviceRepository devices, CancellationToken ct)
    {
        var localRows = await devices.ListLocalSelfDevicesAsync(
            _identity.LocalDeviceId,
            _identity.SignPublicKey,
            _identity.FingerprintHex,
            ct);

        foreach (var localRow in localRows)
            devices.Delete(localRow);
    }


    public async Task EnsureLocalUserDeviceAsync(
        IDeviceRepository devices,
        ILocalUserDeviceRepository localUserDevices,
        Guid userId,
        CancellationToken ct)
    {
        await RemoveLocalDeviceRowsAsync(devices, ct);
        var link = await localUserDevices.GetAsync(userId, ct);
        if (link is null)
        {
            var localUserDevice = new LocalUserDevice
            {
                UserId = userId,
                LocalDeviceIdentityId = _identity.LocalDeviceId,
                IsSyncOn = true
            };
            localUserDevice.GenerateIntegrityHash();
            await localUserDevices.AddAsync(localUserDevice, ct);
            return;
        }

        link.VerifyIntegrity();
        link.LocalDeviceIdentityId = _identity.LocalDeviceId;
        link.GenerateIntegrityHash();
        localUserDevices.Update(link);
    }
}
