using PasswordManagerLocal.Windows.Ipc.Authorization;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Agent.Backend;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Windows.Agent.Localization;

namespace PasswordManagerLocal.Windows.Agent.Hosting;

public sealed class WindowsAgentOperationAuthorizer : IWindowsIpcOperationAuthorizer
{
    private readonly IWindowsAgentStateSource _stateSource;
    private readonly IWindowsAgentAdmissionGate _admissionGate;
    private readonly IWindowsAgentBackendRuntimeOwner _backendOwner;
    private readonly IWindowsIpcOperationAuthorizer _innerAuthorizer;
    private readonly IAgentLocalizer _localizer;

    public WindowsAgentOperationAuthorizer(
        IWindowsAgentStateSource stateSource,
        IWindowsAgentAdmissionGate admissionGate,
        IWindowsAgentBackendRuntimeOwner backendOwner,
        IWindowsIpcOperationAuthorizer innerAuthorizer,
        IAgentLocalizer localizer)
    {
        _stateSource = stateSource ?? throw new ArgumentNullException(nameof(stateSource));
        _admissionGate = admissionGate ?? throw new ArgumentNullException(nameof(admissionGate));
        _backendOwner = backendOwner ?? throw new ArgumentNullException(nameof(backendOwner));
        _innerAuthorizer = innerAuthorizer ?? throw new ArgumentNullException(nameof(innerAuthorizer));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    public IpcAuthorizationDecision Authorize(IpcRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (IsStatusOperation(context.Request.OperationId))
            return LocalizeInnerDecision(context, _innerAuthorizer.Authorize(context));

        var backend = _backendOwner.Snapshot;
        if (_stateSource.State != AgentState.Running ||
            !_admissionGate.IsOpen ||
            backend.IsResetting ||
            backend.RequiresProcessRestart ||
            backend.State is WindowsAgentBackendOwnerState.Resetting or
                WindowsAgentBackendOwnerState.RestartRequired or
                WindowsAgentBackendOwnerState.Stopping or
                WindowsAgentBackendOwnerState.Stopped ||
            (backend.State == WindowsAgentBackendOwnerState.Failed &&
                (backend.Runtime.FailureKind != BackendRuntimeFailureKind.DatabaseCompatibility ||
                    !IsFailedBackendRecoveryOperation(context.Request.OperationId))))
        {
            return IpcAuthorizationDecision.Denied(
                _stateSource.State is AgentState.Stopping or AgentState.Stopped
                    ? IpcErrorCode.AgentStopping
                    : IpcErrorCode.AgentUnavailable,
                IpcErrorCategory.Availability,
                _localizer.GetString(AgentLocalizationKeys.IpcOperationUnavailable),
                isRetryable: true);
        }

        return LocalizeInnerDecision(context, _innerAuthorizer.Authorize(context));
    }

    private IpcAuthorizationDecision LocalizeInnerDecision(
        IpcRequestContext context,
        IpcAuthorizationDecision decision)
    {
        if (decision.IsAuthorized)
            return decision;

        var messageKey = context.Request.OperationId == IpcOperationId.RegisterUiConnection &&
            context.Connection.PeerRole != IpcPeerRole.Ui
            ? AgentLocalizationKeys.IpcUiRegistrationRequired
            : decision.ErrorCode == IpcErrorCode.UiNotRegistered
                ? AgentLocalizationKeys.IpcRegisteredUiRequired
                : AgentLocalizationKeys.IpcUnauthorizedOperation;
        return IpcAuthorizationDecision.Denied(
            decision.ErrorCode,
            decision.ErrorCategory,
            _localizer.GetString(messageKey),
            decision.IsRetryable);
    }

    private static bool IsFailedBackendRecoveryOperation(IpcOperationId operationId) =>
        operationId is IpcOperationId.RegisterUiConnection or
            IpcOperationId.UnregisterUiConnection or
            IpcOperationId.ResetDatabase or
            IpcOperationId.RequestUiOpen or
            IpcOperationId.RequestUiActivation or
            IpcOperationId.ReloadApplicationPreferences;

    private static bool IsStatusOperation(IpcOperationId operationId) =>
        operationId is IpcOperationId.Ping or
            IpcOperationId.GetAgentStatus or
            IpcOperationId.GetBackendRuntimeStatus or
            IpcOperationId.GetInteractiveSessionStatus or
            IpcOperationId.GetSynchronizationStatus or
            IpcOperationId.GetBackgroundSyncState;
}
