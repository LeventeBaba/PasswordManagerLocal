using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IDeviceSecurityService
{
    Task RecordInvalidIncomingSyncAsync(Device device, string reason, CancellationToken ct = default);
    Task ResetInvalidIncomingSyncAsync(Device device, CancellationToken ct = default);
}
