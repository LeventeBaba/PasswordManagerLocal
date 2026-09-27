using PasswordManagerLocal.Windows.Ipc.Authorization;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.EndpointRpc.Authorization;

public sealed class EndpointRpcOperationAuthorizer : IWindowsIpcOperationAuthorizer
{
    private readonly EndpointRpcConnectionAuthorizer _connectionAuthorizer;
    private readonly IEndpointRpcAdmissionPolicy _admissionPolicy;

    public EndpointRpcOperationAuthorizer(
        EndpointRpcConnectionAuthorizer connectionAuthorizer,
        IEndpointRpcAdmissionPolicy admissionPolicy)
    {
        _connectionAuthorizer = connectionAuthorizer
            ?? throw new ArgumentNullException(nameof(connectionAuthorizer));
        _admissionPolicy = admissionPolicy
            ?? throw new ArgumentNullException(nameof(admissionPolicy));
    }

    public IpcAuthorizationDecision Authorize(IpcRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_admissionPolicy.CanAcceptConnection)
        {
            return IpcAuthorizationDecision.Denied(
                IpcErrorCode.AgentUnavailable,
                IpcErrorCategory.Availability,
                "The Windows agent endpoint channel is unavailable.",
                isRetryable: true);
        }

        if (!_connectionAuthorizer.IsAuthorizedConnection(context.Connection))
        {
            return IpcAuthorizationDecision.Denied(
                IpcErrorCode.UnauthorizedOperation,
                IpcErrorCategory.Validation,
                "The endpoint operation is not authorized.");
        }

        return context.Request.OperationId is IpcOperationId.EndpointRpcRequest or IpcOperationId.EndpointSessionReady
            ? IpcAuthorizationDecision.Allowed
            : IpcAuthorizationDecision.Denied(
                IpcErrorCode.UnauthorizedOperation,
                IpcErrorCategory.Validation,
                "The operation is not available on the endpoint channel.");
    }
}
