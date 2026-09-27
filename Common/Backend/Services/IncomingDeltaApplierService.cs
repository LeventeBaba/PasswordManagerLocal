using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class IncomingDeltaApplierService : IIncomingDeltaApplierService
{
    private readonly INetworkDeltaService _networkDeltas;

    public IncomingDeltaApplierService(INetworkDeltaService networkDeltas)
    {
        _networkDeltas = networkDeltas;
    }




    public Task<NetworkDeltaApplyResult> ApplyAsync(NetworkDelta delta, CancellationToken ct = default) =>
        _networkDeltas.ApplyAsync(delta, ct);
}
