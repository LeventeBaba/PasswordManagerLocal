using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Contracts.Responses;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class DeviceService : IDeviceService
{
    private readonly ILocalDeviceSettingsService _localSettings;
    private readonly IUserDeviceQueryService _queries;
    private readonly IUserDeviceSettingsService _settings;
    private readonly IUserDeviceDisconnectionService _disconnection;

    public DeviceService(
        ILocalDeviceSettingsService localSettings,
        IUserDeviceQueryService queries,
        IUserDeviceSettingsService settings,
        IUserDeviceDisconnectionService disconnection)
    {
        _localSettings = localSettings;
        _queries = queries;
        _settings = settings;
        _disconnection = disconnection;
    }

    public Task<LocalDeviceInfoResponse> GetLocalDeviceInfoAsync(CancellationToken ct = default) =>
        _localSettings.GetLocalDeviceInfoAsync(ct);

    public Task<bool> GetLocalUserSyncOnAsync(Guid token, CancellationToken ct = default) =>
        _localSettings.GetLocalUserSyncOnAsync(token, ct);

    public Task SetLocalUserSyncOnAsync(Guid token, bool isSyncOn, CancellationToken ct = default) =>
        _localSettings.SetLocalUserSyncOnAsync(token, isSyncOn, ct);

    public Task SetLocalDeviceNameAsync(Guid token, string name, CancellationToken ct = default) =>
        _localSettings.SetLocalDeviceNameAsync(token, name, ct);

    public Task<IReadOnlyList<UserDeviceInfoResponse>> GetUserDevicesAsync(
        Guid token,
        CancellationToken ct = default) =>
        _queries.GetUserDevicesAsync(token, ct);

    public Task SetUserDeviceNameAsync(
        Guid token,
        Guid deviceId,
        string name,
        CancellationToken ct = default) =>
        _settings.SetUserDeviceNameAsync(token, deviceId, name, ct);

    public Task SetUserDeviceSyncOnAsync(
        Guid token,
        Guid deviceId,
        bool isSyncOn,
        CancellationToken ct = default) =>
        _settings.SetUserDeviceSyncOnAsync(token, deviceId, isSyncOn, ct);

    public Task UnblockUserDeviceAsync(Guid token, Guid deviceId, CancellationToken ct = default) =>
        _settings.UnblockUserDeviceAsync(token, deviceId, ct);

    public Task<DeviceRemovalResultResponse> DisconnectUserDeviceAsync(
        Guid token,
        Guid deviceId,
        byte[] masterPassword,
        CancellationToken ct = default) =>
        _disconnection.DisconnectUserDeviceAsync(token, deviceId, masterPassword, ct);
}
