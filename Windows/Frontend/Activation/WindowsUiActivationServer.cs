using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.Ipc.Authorization;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Ipc.Transport;
using PasswordManagerLocal.Windows.Ipc.Validation;

namespace PasswordManagerLocal.Windows.Frontend.Activation;

public sealed class WindowsUiActivationServer : IWindowsUiActivationServerLifetime
{
    private readonly IWindowsIpcServerHost _serverHost;
    private readonly object _stopGate = new();
    private Task? _stopTask;

    public WindowsUiActivationServer(IWindowsIpcServerHost serverHost)
    {
        _serverHost = serverHost ?? throw new ArgumentNullException(nameof(serverHost));
    }

    public WindowsUiActivationServer(
        string pipeName,
        IWindowsWindowActivationBridge activationBridge,
        IWindowsUiShutdownBridge shutdownBridge,
        IIntentionalAgentShutdownCoordinator shutdownCoordinator,
        Func<int?> trustedAgentProcessIdProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(activationBridge);
        ArgumentNullException.ThrowIfNull(shutdownBridge);
        ArgumentNullException.ThrowIfNull(shutdownCoordinator);
        ArgumentNullException.ThrowIfNull(trustedAgentProcessIdProvider);
        var serializer = new WindowsIpcSerializer();
        var validator = new WindowsIpcContractValidator();
        var dispatcher = new WindowsIpcRequestDispatcher(
            new IWindowsIpcRequestHandler[]
            {
                new RequestUiActivationWindowsIpcRequestHandler(
                    new WindowsUiActivationRequestSink(
                        activationBridge,
                        shutdownBridge,
                        shutdownCoordinator))
            },
            validator,
            new WindowsUiActivationOperationAuthorizer(trustedAgentProcessIdProvider));
        var options = new WindowsIpcServerOptions(
            IpcPeerRole.Ui,
            new[] { IpcPeerRole.Agent, IpcPeerRole.Ui, IpcPeerRole.TestClient },
            IpcCapabilities.UiActivation,
            maximumActiveRequestsPerConnection: 4,
            managesUiRegistration: false);
        var factory = new WindowsIpcServerConnectionSessionFactory(
            serializer,
            dispatcher,
            options,
            contractValidator: validator);
        _serverHost = new WindowsIpcServerHost(
            new WindowsNamedPipeServer(pipeName, new IpcFrameCodec()),
            factory,
            new WindowsIpcServerHostOptions(maximumActiveConnections: 4));
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        _serverHost.StartAsync(cancellationToken);

    public void RequestStop() => _ = GetStopTask();

    public async ValueTask DisposeAsync()
    {
        await GetStopTask();
        await _serverHost.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private Task GetStopTask()
    {
        lock (_stopGate)
            return _stopTask ??= _serverHost.StopAsync();
    }
}
