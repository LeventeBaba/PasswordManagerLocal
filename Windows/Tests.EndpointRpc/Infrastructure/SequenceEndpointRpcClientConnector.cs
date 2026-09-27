using PasswordManagerLocal.Windows.EndpointRpc.Client;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class SequenceEndpointRpcClientConnector : IEndpointRpcClientConnector
{
    private readonly Queue<IEndpointRpcTransport> _transports;

    public SequenceEndpointRpcClientConnector(params IEndpointRpcTransport[] transports) =>
        _transports = new Queue<IEndpointRpcTransport>(
            transports ?? throw new ArgumentNullException(nameof(transports)));

    public int ConnectCount { get; private set; }

    public Task<IEndpointRpcTransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectCount++;
        if (_transports.Count == 0)
            throw new InvalidOperationException("No endpoint transport is available.");
        return Task.FromResult(_transports.Dequeue());
    }
}
