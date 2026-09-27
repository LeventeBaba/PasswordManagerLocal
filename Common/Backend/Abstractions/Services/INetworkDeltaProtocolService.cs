using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface INetworkDeltaProtocolService
{
    Task<ValidatedNetworkDelta> ValidateAndReadAsync(NetworkDelta delta, CancellationToken ct = default);
    Task ValidateSourceAuthorizationAsync(Device sourceDevice, SyncDeltaPayload payload, CancellationToken ct = default);
}
