using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserDeviceQueryService
{
    Task<IReadOnlyList<UserDeviceInfoResponse>> GetUserDevicesAsync(Guid token, CancellationToken ct = default);
}
