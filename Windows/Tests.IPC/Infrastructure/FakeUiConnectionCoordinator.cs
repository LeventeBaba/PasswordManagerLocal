using PasswordManagerLocal.Windows.Ipc.Lifecycle;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeUiConnectionCoordinator : IUiConnectionCoordinator
{
    private bool _intentionalShutdownActive;

    public Guid? RegisteredConnectionId => Registration?.ConnectionId;
    public UiConnectionRegistration? Registration { get; set; }
    public bool RejectShutdownCoordination { get; set; }
    public int BeginShutdownCount { get; private set; }
    public int CancelShutdownCount { get; private set; }
    public event EventHandler<UiConnectionRegistrationChangedEventArgs>? RegistrationChanged;

    public bool TryRegister(IpcConnectionContext connection, out UiConnectionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (_intentionalShutdownActive)
        {
            registration = Registration ?? CreateRegistration();
            return false;
        }

        registration = new UiConnectionRegistration(
            connection.ConnectionId,
            connection.PeerProcessId,
            connection.PeerWindowsSessionId,
            connection.PeerSessionId,
            (Registration?.Generation ?? 0) + 1);
        var previous = Registration;
        Registration = registration;
        RegistrationChanged?.Invoke(
            this,
            new UiConnectionRegistrationChangedEventArgs(previous, registration));
        return true;
    }

    public bool Unregister(Guid connectionId)
    {
        if (Registration?.ConnectionId != connectionId)
            return false;
        var previous = Registration;
        Registration = null;
        RegistrationChanged?.Invoke(
            this,
            new UiConnectionRegistrationChangedEventArgs(previous, null));
        return true;
    }

    public bool IsCurrentRegistration(
        int processId,
        int windowsSessionId,
        Guid instanceId,
        long? generation = null) =>
        Registration is { } registration &&
        registration.ProcessId == processId &&
        registration.WindowsSessionId == windowsSessionId &&
        registration.InstanceId == instanceId &&
        (!generation.HasValue || registration.Generation == generation.Value);

    public bool TryBeginIntentionalShutdown(out UiConnectionRegistration? registration)
    {
        BeginShutdownCount++;
        registration = Registration;
        if (RejectShutdownCoordination || _intentionalShutdownActive)
            return false;
        _intentionalShutdownActive = true;
        return true;
    }

    public void CancelIntentionalShutdown()
    {
        CancelShutdownCount++;
        _intentionalShutdownActive = false;
    }

    public static UiConnectionRegistration CreateRegistration() => new(
        Guid.NewGuid(),
        101,
        1,
        Guid.NewGuid(),
        1);
}
