namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

public interface IEndpointRpcClientConnector
{
    Task<IEndpointRpcTransport> ConnectAsync(CancellationToken cancellationToken = default);
}
