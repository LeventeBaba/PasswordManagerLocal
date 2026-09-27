using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface INetworkDeltaService
{
    Task<NetworkDeltaApplyResult> ApplyAsync(NetworkDelta delta, CancellationToken ct = default);
    Task<NetworkDelta> BuildAsync(SyncItem item, Device device, CancellationToken ct = default);
}
