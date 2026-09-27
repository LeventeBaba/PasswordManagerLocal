using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.EndpointRpc.Authorization;

public sealed class EndpointRpcConnectionAuthorizer :
    IWindowsIpcHandshakeAuthorizer,
    IWindowsIpcConnectionLifecycleObserver
{
    private readonly IEndpointUiRegistrationResolver _registrationResolver;
    private readonly IEndpointRpcAdmissionPolicy _admissionPolicy;
    private readonly object _gate = new();
    private Guid? _activeConnectionId;
    private long _activeRegistrationGeneration;
    private int _activeProcessId;
    private int _activeWindowsSessionId;
    private Guid _activeInstanceId;

    public EndpointRpcConnectionAuthorizer(
        IEndpointUiRegistrationResolver registrationResolver,
        IEndpointRpcAdmissionPolicy admissionPolicy)
    {
        _registrationResolver = registrationResolver
            ?? throw new ArgumentNullException(nameof(registrationResolver));
        _admissionPolicy = admissionPolicy
            ?? throw new ArgumentNullException(nameof(admissionPolicy));
    }

    public IpcHandshakeAuthorizationDecision Authorize(IpcConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!_admissionPolicy.CanAcceptConnection)
        {
            return IpcHandshakeAuthorizationDecision.Reject(
                IpcErrorCode.AgentUnavailable,
                "The Windows agent endpoint channel is unavailable.");
        }

        if (connection.PeerRole != IpcPeerRole.Ui)
        {
            return IpcHandshakeAuthorizationDecision.Reject(
                IpcErrorCode.UnexpectedPeerRole,
                "Only the registered UI may open the endpoint channel.");
        }

        if ((connection.PeerCapabilities & IpcCapabilities.EndpointRpc) == 0)
        {
            return IpcHandshakeAuthorizationDecision.Reject(
                IpcErrorCode.UnsupportedCapability,
                "The endpoint RPC capability is required.");
        }

        if (!_registrationResolver.TryResolve(
                connection.PeerProcessId,
                connection.PeerWindowsSessionId,
                connection.PeerSessionId,
                out var registrationGeneration))
        {
            return IpcHandshakeAuthorizationDecision.Reject(
                IpcErrorCode.UiNotRegistered,
                "The UI is not registered for endpoint access.");
        }

        lock (_gate)
        {
            if (!_admissionPolicy.CanAcceptConnection)
            {
                return IpcHandshakeAuthorizationDecision.Reject(
                    IpcErrorCode.AgentUnavailable,
                    "The Windows agent endpoint channel is unavailable.");
            }
            if (!_registrationResolver.IsCurrent(
                    connection.PeerProcessId,
                    connection.PeerWindowsSessionId,
                    connection.PeerSessionId,
                    registrationGeneration))
            {
                return IpcHandshakeAuthorizationDecision.Reject(
                    IpcErrorCode.UiNotRegistered,
                    "The UI registration changed before endpoint authorization completed.");
            }

            if (_activeConnectionId.HasValue &&
                !_registrationResolver.IsCurrent(
                    _activeProcessId,
                    _activeWindowsSessionId,
                    _activeInstanceId,
                    _activeRegistrationGeneration))
            {
                ClearActiveConnection();
            }

            if (_activeConnectionId.HasValue &&
                _activeConnectionId.Value != connection.ConnectionId)
            {
                return IpcHandshakeAuthorizationDecision.Reject(
                    IpcErrorCode.UiAlreadyRegistered,
                    "An endpoint connection is already active.");
            }

            _activeConnectionId = connection.ConnectionId;
            _activeRegistrationGeneration = registrationGeneration;
            _activeProcessId = connection.PeerProcessId;
            _activeWindowsSessionId = connection.PeerWindowsSessionId;
            _activeInstanceId = connection.PeerSessionId;
            return IpcHandshakeAuthorizationDecision.Authorized;
        }
    }

    public bool IsAuthorizedConnection(IpcConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_gate)
        {
            return _admissionPolicy.CanAcceptConnection &&
                _activeConnectionId == connection.ConnectionId &&
                _activeProcessId == connection.PeerProcessId &&
                _activeWindowsSessionId == connection.PeerWindowsSessionId &&
                _activeInstanceId == connection.PeerSessionId &&
                _registrationResolver.IsCurrent(
                    connection.PeerProcessId,
                    connection.PeerWindowsSessionId,
                    connection.PeerSessionId,
                    _activeRegistrationGeneration);
        }
    }

    public ValueTask OnConnectionLifecycleChangedAsync(
        IpcConnectionLifecycleNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (notification.State is not IpcConnectionLifecycleState.Disconnected and
            not IpcConnectionLifecycleState.Faulted)
        {
            return ValueTask.CompletedTask;
        }

        lock (_gate)
        {
            if (_activeConnectionId == notification.ConnectionId)
                ClearActiveConnection();
        }

        return ValueTask.CompletedTask;
    }
    private void ClearActiveConnection()
    {
        _activeConnectionId = null;
        _activeRegistrationGeneration = 0;
        _activeProcessId = 0;
        _activeWindowsSessionId = 0;
        _activeInstanceId = Guid.Empty;
    }
}
