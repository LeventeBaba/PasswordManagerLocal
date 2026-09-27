using PasswordManagerLocal.Common.Contracts.Endpoints;

namespace PasswordManagerLocal.Windows.EndpointRpc.Server;

public sealed class FixedEndpointRpcEndpointAdapter : IEndpointRpcEndpointAdapter, IEndpointRpcSessionReadiness
{
    private readonly IEndpoints _endpoints;

    public FixedEndpointRpcEndpointAdapter(IEndpoints endpoints) =>
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));

    public IEndpoints GetEndpoints(EndpointRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _endpoints;
    }

    public bool IsReady(PasswordManagerLocal.Windows.Ipc.Lifecycle.IpcConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return true;
    }
}
