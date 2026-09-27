using PasswordManagerLocal.Windows.Ipc.Transport;
using System.Threading.Channels;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class QueuedEndpointConnectionListener :
    IWindowsIpcConnectionListener,
    IAsyncDisposable
{
    private readonly Channel<IWindowsIpcConnection> _connections =
        Channel.CreateUnbounded<IWindowsIpcConnection>();

    public void Enqueue(IWindowsIpcConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!_connections.Writer.TryWrite(connection))
            throw new InvalidOperationException("The test connection could not be queued.");
    }

    public Task<IWindowsIpcConnection> AcceptAsync(
        CancellationToken cancellationToken = default) =>
        _connections.Reader.ReadAsync(cancellationToken).AsTask();

    public ValueTask DisposeAsync()
    {
        _connections.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
