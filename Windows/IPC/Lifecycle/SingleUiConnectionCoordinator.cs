namespace PasswordManagerLocal.Windows.Ipc.Lifecycle;

public sealed class SingleUiConnectionCoordinator : IUiConnectionCoordinator
{
    private readonly object _gate = new();
    private UiConnectionRegistration? _registration;
    private long _generation;
    private bool _intentionalShutdownActive;

    public Guid? RegisteredConnectionId
    {
        get
        {
            lock (_gate)
                return _registration?.ConnectionId;
        }
    }

    public UiConnectionRegistration? Registration
    {
        get
        {
            lock (_gate)
                return _registration;
        }
    }

    public event EventHandler<UiConnectionRegistrationChangedEventArgs>? RegistrationChanged;

    public bool TryRegister(
        IpcConnectionContext connection,
        out UiConnectionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.PeerRole != Protocol.IpcPeerRole.Ui)
            throw new ArgumentException("Only a UI connection may be registered.", nameof(connection));

        UiConnectionRegistration? previous = null;
        var changed = false;
        lock (_gate)
        {
            if (_intentionalShutdownActive)
            {
                registration = _registration ?? new UiConnectionRegistration(
                    connection.ConnectionId,
                    connection.PeerProcessId,
                    connection.PeerWindowsSessionId,
                    connection.PeerSessionId,
                    _generation);
                return false;
            }

            if (_registration is not null &&
                _registration.ConnectionId != connection.ConnectionId &&
                !MatchesIdentity(_registration, connection))
            {
                registration = _registration;
                return false;
            }

            if (_registration?.ConnectionId == connection.ConnectionId)
            {
                registration = _registration;
                return true;
            }

            previous = _registration;
            registration = new UiConnectionRegistration(
                connection.ConnectionId,
                connection.PeerProcessId,
                connection.PeerWindowsSessionId,
                connection.PeerSessionId,
                checked(++_generation));
            _registration = registration;
            changed = true;
        }

        if (changed)
            RaiseRegistrationChanged(previous, registration);
        return true;
    }

    public bool Unregister(Guid connectionId)
    {
        if (connectionId == Guid.Empty)
            throw new ArgumentException("The connection ID cannot be empty.", nameof(connectionId));

        UiConnectionRegistration? previous;
        lock (_gate)
        {
            if (_registration?.ConnectionId != connectionId)
                return false;

            previous = _registration;
            _registration = null;
        }

        RaiseRegistrationChanged(previous, null);
        return true;
    }

    public bool IsCurrentRegistration(
        int processId,
        int windowsSessionId,
        Guid instanceId,
        long? generation = null)
    {
        lock (_gate)
        {
            return _registration is { } registration &&
                registration.ProcessId == processId &&
                registration.WindowsSessionId == windowsSessionId &&
                registration.InstanceId == instanceId &&
                (!generation.HasValue || registration.Generation == generation.Value);
        }
    }

    public bool TryBeginIntentionalShutdown(out UiConnectionRegistration? registration)
    {
        lock (_gate)
        {
            registration = _registration;
            if (_intentionalShutdownActive)
                return false;

            _intentionalShutdownActive = true;
            return true;
        }
    }

    public void CancelIntentionalShutdown()
    {
        lock (_gate)
            _intentionalShutdownActive = false;
    }

    private void RaiseRegistrationChanged(
        UiConnectionRegistration? previous,
        UiConnectionRegistration? current)
    {
        var handlers = RegistrationChanged;
        if (handlers is null)
            return;

        var args = new UiConnectionRegistrationChangedEventArgs(previous, current);
        foreach (EventHandler<UiConnectionRegistrationChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); } catch { }
        }
    }

    private static bool MatchesIdentity(
        UiConnectionRegistration registration,
        IpcConnectionContext connection) =>
        registration.ProcessId == connection.PeerProcessId &&
        registration.WindowsSessionId == connection.PeerWindowsSessionId &&
        registration.InstanceId == connection.PeerSessionId;
}
