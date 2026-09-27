using PasswordManagerLocal.Windows.EndpointRpc.Authorization;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.EndpointRpc.Server.LargeTransfer;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Ipc.Transport;
using PasswordManagerLocal.Windows.Ipc.Validation;

namespace PasswordManagerLocal.Windows.EndpointRpc.Server;

public sealed class EndpointRpcServerSessionFactory : IEndpointRpcServerSessionFactory
{
    public const int MaximumActiveEndpointRequests = 32;

    private readonly WindowsIpcServerConnectionSessionFactory _innerFactory;
    private readonly EndpointLargeResultTransferStore _largeResultTransferStore;
    private int _disposed;

    public EndpointRpcServerSessionFactory(
        IEndpointRpcEndpointAdapter endpointAdapter,
        EndpointRpcConnectionAuthorizer connectionAuthorizer,
        IEndpointRpcAdmissionPolicy admissionPolicy,
        IEndpointRpcSessionReadiness? sessionReadiness = null,
        IEnumerable<PasswordManagerLocal.Windows.Ipc.Lifecycle.IWindowsIpcConnectionLifecycleObserver>? observers = null)
    {
        ArgumentNullException.ThrowIfNull(endpointAdapter);
        ArgumentNullException.ThrowIfNull(connectionAuthorizer);
        ArgumentNullException.ThrowIfNull(admissionPolicy);

        var serializer = new EndpointRpcSerializer();
        var validator = new EndpointRpcContractValidator();
        var codec = new EndpointRpcMessageCodec(serializer);
        var errorMapper = new EndpointRpcBackendErrorMapper();
        _largeResultTransferStore = new EndpointLargeResultTransferStore();
        var dispatcher = new EndpointRpcDispatcher(
            endpointAdapter,
            serializer,
            validator,
            errorMapper,
            _largeResultTransferStore,
            endpointAdapter as IEndpointRpcRestartRequirementHandler);
        var requestHandler = new EndpointRpcWindowsIpcRequestHandler(
            dispatcher,
            codec,
            validator,
            serializer,
            _largeResultTransferStore,
            errorMapper,
            admissionPolicy);
        var readiness = sessionReadiness ?? endpointAdapter as IEndpointRpcSessionReadiness
            ?? throw new ArgumentException("The endpoint adapter must provide session readiness.", nameof(endpointAdapter));
        var ipcDispatcher = new WindowsIpcRequestDispatcher(
            [requestHandler, new EndpointSessionReadyWindowsIpcRequestHandler(readiness, admissionPolicy)],
            new WindowsIpcContractValidator(),
            new EndpointRpcOperationAuthorizer(connectionAuthorizer, admissionPolicy));
        var options = new WindowsIpcServerOptions(
            IpcPeerRole.Agent,
            [IpcPeerRole.Ui],
            IpcCapabilities.EndpointRpc,
            MaximumActiveEndpointRequests,
            managesUiRegistration: false,
            requiredClientCapabilities: IpcCapabilities.EndpointRpc);

        _innerFactory = new WindowsIpcServerConnectionSessionFactory(
            new WindowsIpcSerializer(),
            ipcDispatcher,
            options,
            observers: [connectionAuthorizer, .. (observers ?? Array.Empty<PasswordManagerLocal.Windows.Ipc.Lifecycle.IWindowsIpcConnectionLifecycleObserver>()), _largeResultTransferStore],
            uiCoordinator: null,
            contractValidator: new WindowsIpcContractValidator(),
            handshakeAuthorizer: connectionAuthorizer);
    }

    public IEndpointRpcServerSession Create(IWindowsIpcConnection connection)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(EndpointRpcServerSessionFactory));
        return new EndpointRpcServerSession(_innerFactory.Create(connection));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _largeResultTransferStore.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    IWindowsIpcServerSession IWindowsIpcServerSessionFactory.Create(
        IWindowsIpcConnection connection) => Create(connection);
}
