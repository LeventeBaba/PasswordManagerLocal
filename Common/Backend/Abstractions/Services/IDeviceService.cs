namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IDeviceService :
    ILocalDeviceSettingsService,
    IUserDeviceQueryService,
    IUserDeviceSettingsService,
    IUserDeviceDisconnectionService
{
}
