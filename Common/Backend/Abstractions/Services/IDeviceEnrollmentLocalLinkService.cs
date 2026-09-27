using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IDeviceEnrollmentLocalLinkService
{
    Task RemoveLocalDeviceRowsAsync(IDeviceRepository devices, CancellationToken ct = default);
    Task EnsureLocalUserDeviceAsync(
        IDeviceRepository devices,
        ILocalUserDeviceRepository localUserDevices,
        Guid userId,
        CancellationToken ct = default);
}
