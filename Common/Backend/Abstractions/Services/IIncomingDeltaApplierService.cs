using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IIncomingDeltaApplierService
{
    Task<NetworkDeltaApplyResult> ApplyAsync(NetworkDelta delta, CancellationToken ct = default);
}
