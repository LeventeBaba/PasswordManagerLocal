using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IPendingSyncActivationService
{
    void ActivateDevices(IReadOnlyList<Device> devices);
    Task ActivatePendingAsync(CancellationToken ct = default);
}
