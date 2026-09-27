using PasswordManagerLocal.Windows.EndpointRpc.Client;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class FakeEndpointRpcClientConnector : IEndpointRpcClientConnector
{
    private readonly IEndpointRpcTransport _transport;

    public FakeEndpointRpcClientConnector(IEndpointRpcTransport transport) =>
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public int ConnectCount { get; private set; }

    public Task<IEndpointRpcTransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectCount++;
        return Task.FromResult(_transport);
    }
}
