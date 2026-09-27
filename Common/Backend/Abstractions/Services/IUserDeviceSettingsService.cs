namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserDeviceSettingsService
{
    Task SetUserDeviceNameAsync(Guid token, Guid deviceId, string name, CancellationToken ct = default);
    Task SetUserDeviceSyncOnAsync(Guid token, Guid deviceId, bool isSyncOn, CancellationToken ct = default);
    Task UnblockUserDeviceAsync(Guid token, Guid deviceId, CancellationToken ct = default);
}
