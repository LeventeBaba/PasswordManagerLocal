using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Windows.EndpointRpc.Server;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

internal sealed class FixedEndpointRpcEndpointAdapter : IEndpointRpcEndpointAdapter, IEndpointRpcSessionReadiness
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
