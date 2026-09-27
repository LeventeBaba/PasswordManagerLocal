using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ILocalDeviceMatcherService
{
    bool IsLocalDevice(Device device);
}
