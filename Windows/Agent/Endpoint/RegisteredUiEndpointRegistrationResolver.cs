using PasswordManagerLocal.Windows.EndpointRpc.Authorization;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;

namespace PasswordManagerLocal.Windows.Agent.Endpoint;

public sealed class RegisteredUiEndpointRegistrationResolver :
    IEndpointUiRegistrationResolver,
    IDisposable
{
    private readonly IUiConnectionCoordinator _coordinator;
    private readonly IWindowsAgentAdmissionGate _admissionGate;
    private int _disposed;

    public RegisteredUiEndpointRegistrationResolver(
        IUiConnectionCoordinator coordinator,
        IWindowsAgentAdmissionGate admissionGate)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _admissionGate = admissionGate ?? throw new ArgumentNullException(nameof(admissionGate));
        _coordinator.RegistrationChanged += HandleRegistrationChanged;
    }

    public event EventHandler<UiConnectionRegistrationChangedEventArgs>? RegistrationChanged;

    public bool TryResolve(
        int processId,
        int windowsSessionId,
        Guid instanceId,
        out long registrationGeneration)
    {
        if (!_admissionGate.IsOpen)
        {
            registrationGeneration = 0;
            return false;
        }

        var registration = _coordinator.Registration;
        if (registration is null ||
            registration.ProcessId != processId ||
            registration.WindowsSessionId != windowsSessionId ||
            registration.InstanceId != instanceId)
        {
            registrationGeneration = 0;
            return false;
        }

        registrationGeneration = registration.Generation;
        return true;
    }

    public bool IsCurrent(
        int processId,
        int windowsSessionId,
        Guid instanceId,
        long registrationGeneration) =>
        _admissionGate.IsOpen &&
        _coordinator.IsCurrentRegistration(
            processId,
            windowsSessionId,
            instanceId,
            registrationGeneration);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _coordinator.RegistrationChanged -= HandleRegistrationChanged;
        RegistrationChanged = null;
        GC.SuppressFinalize(this);
    }

    private void HandleRegistrationChanged(
        object? sender,
        UiConnectionRegistrationChangedEventArgs args)
    {
        var handlers = RegistrationChanged;
        if (handlers is null)
            return;

        foreach (EventHandler<UiConnectionRegistrationChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); } catch { }
        }
    }
}
