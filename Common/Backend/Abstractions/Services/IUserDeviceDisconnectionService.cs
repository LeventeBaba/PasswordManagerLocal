using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserDeviceDisconnectionService
{
    Task<DeviceRemovalResultResponse> DisconnectUserDeviceAsync(
        Guid token,
        Guid deviceId,
        byte[] masterPassword,
        CancellationToken ct = default);
}
