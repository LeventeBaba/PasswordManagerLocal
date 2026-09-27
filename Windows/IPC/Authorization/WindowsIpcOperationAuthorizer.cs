using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Ipc.Authorization;

public sealed class WindowsIpcOperationAuthorizer : IWindowsIpcOperationAuthorizer
{
    private readonly IUiConnectionCoordinator _uiCoordinator;

    public WindowsIpcOperationAuthorizer(IUiConnectionCoordinator uiCoordinator)
    {
        _uiCoordinator = uiCoordinator ?? throw new ArgumentNullException(nameof(uiCoordinator));
    }

    public IpcAuthorizationDecision Authorize(IpcRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Request.OperationId switch
        {
            IpcOperationId.Ping or
            IpcOperationId.GetAgentStatus or
            IpcOperationId.GetBackendRuntimeStatus or
            IpcOperationId.GetInteractiveSessionStatus or
            IpcOperationId.GetSynchronizationStatus or
            IpcOperationId.GetBackgroundSyncState or
            IpcOperationId.RequestUiOpen or
            IpcOperationId.RequestUiActivation => IpcAuthorizationDecision.Allowed,
            IpcOperationId.RegisterUiConnection => AuthorizeRegistration(context),
            IpcOperationId.UnregisterUiConnection or
            IpcOperationId.RequestAgentExit or
            IpcOperationId.ResetDatabase or
            IpcOperationId.SetBackgroundSyncEnabled or
            IpcOperationId.ReloadApplicationPreferences => AuthorizeRegisteredUi(context),
            _ => IpcAuthorizationDecision.Denied(
                IpcErrorCode.UnauthorizedOperation,
                IpcErrorCategory.Validation,
                "The IPC operation is not authorized for this connection.")
        };
    }

    private static IpcAuthorizationDecision AuthorizeRegistration(IpcRequestContext context) =>
        context.Connection.PeerRole == IpcPeerRole.Ui
            ? IpcAuthorizationDecision.Allowed
            : IpcAuthorizationDecision.Denied(
                IpcErrorCode.UnauthorizedOperation,
                IpcErrorCategory.Validation,
                "Only a UI connection can register as the interactive UI.");

    private IpcAuthorizationDecision AuthorizeRegisteredUi(IpcRequestContext context)
    {
        if (context.Connection.PeerRole != IpcPeerRole.Ui ||
            _uiCoordinator.RegisteredConnectionId != context.Connection.ConnectionId)
        {
            return IpcAuthorizationDecision.Denied(
                IpcErrorCode.UiNotRegistered,
                IpcErrorCategory.Conflict,
                "The IPC operation requires the registered UI connection.",
                isRetryable: true);
        }

        return IpcAuthorizationDecision.Allowed;
    }
}
