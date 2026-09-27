using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Ipc.Authorization;

public sealed class WindowsUiActivationOperationAuthorizer : IWindowsIpcOperationAuthorizer
{
    private readonly Func<int?> _trustedAgentProcessIdProvider;

    public WindowsUiActivationOperationAuthorizer(
        Func<int?>? trustedAgentProcessIdProvider = null) =>
        _trustedAgentProcessIdProvider = trustedAgentProcessIdProvider ?? (() => null);

    public IpcAuthorizationDecision Authorize(IpcRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Request.OperationId != IpcOperationId.RequestUiActivation)
        {
            return IpcAuthorizationDecision.Denied(
                IpcErrorCode.UnauthorizedOperation,
                IpcErrorCategory.Validation,
                "Only UI activation is supported on this connection.");
        }

        UiActivationRequestDto request;
        try
        {
            request = context.GetRequiredPayload(WindowsIpcJsonContext.Default.UiActivationRequestDto);
        }
        catch
        {
            return IpcAuthorizationDecision.Denied(
                IpcErrorCode.InvalidPayload,
                IpcErrorCategory.Validation,
                "The UI activation request is invalid.");
        }

        if (request.Command != UiActivationCommand.IntentionalAgentShutdown)
            return IpcAuthorizationDecision.Allowed;

        var trustedAgentProcessId = _trustedAgentProcessIdProvider();
        if (context.Connection.PeerRole != IpcPeerRole.Agent ||
            trustedAgentProcessId is null ||
            context.Connection.PeerProcessId != trustedAgentProcessId.Value)
        {
            return IpcAuthorizationDecision.Denied(
                IpcErrorCode.UnauthorizedOperation,
                IpcErrorCategory.Validation,
                "Only the connected Windows agent may request UI shutdown.");
        }

        return IpcAuthorizationDecision.Allowed;
    }
}
