using PasswordManagerLocal.Windows.Ipc.Protocol;
using System.Threading.Channels;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class InMemoryIpcConnectionPair
{
    public InMemoryIpcConnectionPair()
    {
        var clientToServer = Channel.CreateUnbounded<IpcFrame>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        var serverToClient = Channel.CreateUnbounded<IpcFrame>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        Client = new InMemoryIpcConnection(serverToClient.Reader, clientToServer.Writer);
        Server = new InMemoryIpcConnection(clientToServer.Reader, serverToClient.Writer);
    }

    public InMemoryIpcConnection Client { get; }
    public InMemoryIpcConnection Server { get; }
}
