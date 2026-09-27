using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Transport;
using PasswordManagerLocal.Windows.Ipc.Validation;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public sealed class WindowsIpcServerConnectionSessionFactory : IWindowsIpcServerSessionFactory
{
    private readonly WindowsIpcSerializer _serializer;
    private readonly WindowsIpcRequestDispatcher _dispatcher;
    private readonly WindowsIpcServerOptions _options;
    private readonly IReadOnlyList<IWindowsIpcConnectionLifecycleObserver> _observers;
    private readonly IUiConnectionCoordinator? _uiCoordinator;
    private readonly WindowsIpcContractValidator _contractValidator;
    private readonly IWindowsIpcHandshakeAuthorizer? _handshakeAuthorizer;

    public WindowsIpcServerConnectionSessionFactory(
        WindowsIpcSerializer serializer,
        WindowsIpcRequestDispatcher dispatcher,
        WindowsIpcServerOptions options,
        IEnumerable<IWindowsIpcConnectionLifecycleObserver>? observers = null,
        IUiConnectionCoordinator? uiCoordinator = null,
        WindowsIpcContractValidator? contractValidator = null,
        IWindowsIpcHandshakeAuthorizer? handshakeAuthorizer = null)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _observers = observers?.ToArray() ?? Array.Empty<IWindowsIpcConnectionLifecycleObserver>();
        _uiCoordinator = uiCoordinator;
        _contractValidator = contractValidator ?? new WindowsIpcContractValidator();
        _handshakeAuthorizer = handshakeAuthorizer;
    }

    public IWindowsIpcServerSession Create(IWindowsIpcConnection connection) =>
        new WindowsIpcServerConnectionSession(
            connection,
            _serializer,
            _dispatcher,
            _options,
            _observers,
            _uiCoordinator,
            _contractValidator,
            _handshakeAuthorizer);
}
