using System.Net.Security;

namespace PasswordManagerLocal.Common.Backend.Sync.Tcp;

internal sealed class TcpSyncClientConnection : IAsyncDisposable
{
    private readonly System.Net.Sockets.TcpClient _client;

    public TcpSyncClientConnection(System.Net.Sockets.TcpClient client, SslStream stream)
    {
        _client = client;
        Stream = stream;
    }

    public SslStream Stream { get; }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        _client.Dispose();
    }
}
