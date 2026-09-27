using PasswordManagerLocal.Windows.Agent.Backend;
using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Windows.EndpointRpc.Authorization;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;

namespace PasswordManagerLocal.Windows.Agent.Endpoint;

public sealed class WindowsAgentEndpointAdmissionPolicy : IEndpointRpcAdmissionPolicy
{
    private readonly IWindowsAgentAdmissionGate _admissionGate;
    private readonly IWindowsAgentStateSource _stateSource;
    private readonly IWindowsAgentBackendRuntimeOwner _backendOwner;
    private readonly Func<WindowsAgentEndpointHostState> _endpointHostStateProvider;

    public WindowsAgentEndpointAdmissionPolicy(
        IWindowsAgentAdmissionGate admissionGate,
        IWindowsAgentStateSource stateSource,
        IWindowsAgentBackendRuntimeOwner backendOwner,
        Func<WindowsAgentEndpointHostState> endpointHostStateProvider)
    {
        _admissionGate = admissionGate ?? throw new ArgumentNullException(nameof(admissionGate));
        _stateSource = stateSource ?? throw new ArgumentNullException(nameof(stateSource));
        _backendOwner = backendOwner ?? throw new ArgumentNullException(nameof(backendOwner));
        _endpointHostStateProvider = endpointHostStateProvider
            ?? throw new ArgumentNullException(nameof(endpointHostStateProvider));
    }

    public bool CanAcceptConnection => IsOperational();

    public bool TryEnterRequest(out IDisposable? lease)
    {
        lease = null;
        if (!IsOperational() || !_admissionGate.TryEnter(out var admittedLease))
            return false;

        if (!IsOperational())
        {
            admittedLease!.Dispose();
            return false;
        }

        lease = admittedLease;
        return true;
    }

    public Task WaitForDrainAsync(CancellationToken cancellationToken = default) =>
        _admissionGate.WaitForDrainAsync(cancellationToken);

    private bool IsOperational()
    {
        var backend = _backendOwner.Snapshot;
        return _stateSource.State == AgentState.Running &&
            _admissionGate.IsOpen &&
            _endpointHostStateProvider() == WindowsAgentEndpointHostState.Ready &&
            backend.State is WindowsAgentBackendOwnerState.Ready or WindowsAgentBackendOwnerState.Interactive &&
            backend.Runtime.State is not BackendRuntimeState.Failed and
                not BackendRuntimeState.Stopping &&
            !backend.IsResetting &&
            !backend.RequiresProcessRestart;
    }
}