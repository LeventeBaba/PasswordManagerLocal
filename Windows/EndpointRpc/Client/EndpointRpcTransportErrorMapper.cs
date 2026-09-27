using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

internal static class EndpointRpcTransportErrorMapper
{
    public static EndpointRpcRemoteException Map(IpcError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var endpointError = error.ErrorCode switch
        {
            IpcErrorCode.RequestCancelled => Create(
                EndpointRpcErrorCode.OperationCancelled,
                EndpointRpcErrorCategory.Cancellation,
                "The endpoint operation was cancelled.",
                error),
            IpcErrorCode.RequestPayloadTooLarge => Create(
                EndpointRpcErrorCode.RequestPayloadTooLarge,
                EndpointRpcErrorCategory.Validation,
                "The endpoint request payload exceeds the permitted size.",
                error),
            IpcErrorCode.ResponsePayloadTooLarge or IpcErrorCode.SerializedEnvelopeTooLarge => Create(
                EndpointRpcErrorCode.ResponsePayloadTooLarge,
                EndpointRpcErrorCategory.Validation,
                "The endpoint response payload exceeds the permitted size.",
                error),
            IpcErrorCode.UnexpectedPeerRole or
            IpcErrorCode.UnsupportedCapability or
            IpcErrorCode.UiNotRegistered or
            IpcErrorCode.UnauthorizedOperation => Create(
                EndpointRpcErrorCode.AuthorizationFailed,
                EndpointRpcErrorCategory.Authorization,
                "The endpoint connection is not authorized.",
                error),
            IpcErrorCode.UnknownOperation => Create(
                EndpointRpcErrorCode.UnknownOperation,
                EndpointRpcErrorCategory.Validation,
                "The endpoint operation is not supported.",
                error),
            IpcErrorCode.UiAlreadyRegistered or
            IpcErrorCode.TooManyConnections => Create(
                EndpointRpcErrorCode.Conflict,
                EndpointRpcErrorCategory.Conflict,
                "An interactive endpoint connection is already active.",
                error),
            IpcErrorCode.ServerBusy or
            IpcErrorCode.TooManyRequests or
            IpcErrorCode.AgentStopping or
            IpcErrorCode.AgentUnavailable or
            IpcErrorCode.ConnectionClosed => Create(
                EndpointRpcErrorCode.RuntimeUnavailable,
                EndpointRpcErrorCategory.Availability,
                "The endpoint runtime is not available.",
                error),
            IpcErrorCode.RequestRejected => Create(
                EndpointRpcErrorCode.OperationRejected,
                EndpointRpcErrorCategory.Validation,
                "The endpoint operation was rejected.",
                error),
            _ => Create(
                EndpointRpcErrorCode.BackendFailure,
                EndpointRpcErrorCategory.Internal,
                "The endpoint transport failed.",
                error)
        };

        return new EndpointRpcRemoteException(endpointError);
    }

    private static EndpointRpcError Create(
        EndpointRpcErrorCode errorCode,
        EndpointRpcErrorCategory errorCategory,
        string safeMessage,
        IpcError error) =>
        new(
            errorCode,
            errorCategory,
            safeMessage,
            error.CorrelationId,
            error.OccurredAtUtc,
            error.IsRetryable,
            error.RequiresProcessRestart && errorCode == EndpointRpcErrorCode.RuntimeUnavailable,
            EndpointMutationOutcome.NotApplicable,
            Recovery: null);
}
