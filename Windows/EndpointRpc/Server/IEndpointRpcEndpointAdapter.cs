using PasswordManagerLocal.Common.Contracts.Endpoints;

namespace PasswordManagerLocal.Windows.EndpointRpc.Server;

public interface IEndpointRpcEndpointAdapter
{
    IEndpoints GetEndpoints(EndpointRequestContext context);
}
