using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Ipc.Validation;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class IpcTestSession : IAsyncDisposable
{
    private IpcTestSession(
        InMemoryIpcConnectionPair pair,
        WindowsIpcClient client,
        WindowsIpcServerConnectionSession server,
        Task serverTask,
        WindowsIpcSerializer serializer)
    {
        Pair = pair;
        Client = client;
        Server = server;
        ServerTask = serverTask;
        Serializer = serializer;
    }

    public InMemoryIpcConnectionPair Pair { get; }
    public WindowsIpcClient Client { get; }
    public WindowsIpcServerConnectionSession Server { get; }
    public WindowsIpcSerializer Serializer { get; }
    public Task ServerTask { get; }

    public static async Task<IpcTestSession> CreateAsync(
        IEnumerable<IWindowsIpcRequestHandler> handlers,
        IpcPeerRole clientRole = IpcPeerRole.TestClient,
        IEnumerable<IpcPeerRole>? acceptedClientRoles = null,
        IEnumerable<IWindowsIpcConnectionLifecycleObserver>? observers = null,
        IUiConnectionCoordinator? uiConnectionCoordinator = null,
        int maximumPendingRequests = WindowsIpcClientOptions.DefaultMaximumPendingRequests,
        int maximumActiveRequestsPerConnection = WindowsIpcServerOptions.DefaultMaximumActiveRequestsPerConnection)
    {
        var pair = new InMemoryIpcConnectionPair();
        var serializer = new WindowsIpcSerializer();
        var contractValidator = new WindowsIpcContractValidator();
        var dispatcher = new WindowsIpcRequestDispatcher(handlers, contractValidator);
        var options = new WindowsIpcServerOptions(
            IpcPeerRole.Agent,
            acceptedClientRoles ?? new[] { clientRole },
            IpcCapabilities.Control | IpcCapabilities.Status | IpcCapabilities.UiActivation,
            maximumActiveRequestsPerConnection);
        var server = new WindowsIpcServerConnectionSession(
            pair.Server,
            serializer,
            dispatcher,
            options,
            observers,
            uiConnectionCoordinator,
            contractValidator);
        var serverTask = server.RunAsync();
        var client = new WindowsIpcClient(
            pair.Client,
            serializer,
            new WindowsIpcClientOptions(
                clientRole,
                IpcPeerRole.Agent,
                IpcCapabilities.Control | IpcCapabilities.Status,
                Environment.ProcessId,
                0,
                Guid.NewGuid(),
                maximumPendingRequests),
            contractValidator);
        await client.HandshakeAsync();
        return new IpcTestSession(pair, client, server, serverTask, serializer);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await ServerTask;
        await Server.DisposeAsync();
    }
}
