using PasswordManagerLocal.Windows.Ipc.Transport;
using System.Threading.Channels;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class ChannelWindowsIpcConnectionListener : IWindowsIpcConnectionListener
{
    private readonly Channel<IWindowsIpcConnection> _connections =
        Channel.CreateUnbounded<IWindowsIpcConnection>();
    private Exception? _failure;

    public void Queue(IWindowsIpcConnection connection) =>
        _connections.Writer.TryWrite(connection);

    public void Fail(Exception exception)
    {
        _failure = exception;
        _connections.Writer.TryComplete();
    }

    public async Task<IWindowsIpcConnection> AcceptAsync(
        CancellationToken cancellationToken = default)
    {
        if (_failure is not null)
            throw _failure;

        try
        {
            return await _connections.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException) when (_failure is not null)
        {
            throw _failure;
        }
    }
}
