using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ILocalDeviceSettingsService
{
    Task<LocalDeviceInfoResponse> GetLocalDeviceInfoAsync(CancellationToken ct = default);
    Task<bool> GetLocalUserSyncOnAsync(Guid token, CancellationToken ct = default);
    Task SetLocalUserSyncOnAsync(Guid token, bool isSyncOn, CancellationToken ct = default);
    Task SetLocalDeviceNameAsync(Guid token, string name, CancellationToken ct = default);
}
