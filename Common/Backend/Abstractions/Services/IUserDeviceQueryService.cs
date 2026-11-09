using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserDeviceQueryService
{
    /// <summary>
    /// Authenticates the user and returns stored device metadata with the current cached presence projection.
    /// This read does not initiate remote presence probes.
    /// </summary>
    Task<IReadOnlyList<UserDeviceInfoResponse>> GetUserDevicesAsync(Guid token, CancellationToken ct = default);

}
