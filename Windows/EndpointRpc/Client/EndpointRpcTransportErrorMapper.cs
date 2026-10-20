using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Common.Contracts.Errors;

namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

internal static class EndpointRpcTransportErrorMapper
{
    public static EndpointRpcRemoteException Map(
        IpcError error,
        bool isMutatingOperation = false)
    {
        ArgumentNullException.ThrowIfNull(error);

        var endpointError = error.ErrorCode switch
        {
            IpcErrorCode.RequestCancelled => Create(
                EndpointRpcErrorCode.OperationCancelled,
                EndpointFailureKind.Generic,
                "The endpoint operation was cancelled.",
                error,
                isMutatingOperation),
            IpcErrorCode.RequestPayloadTooLarge => Create(
                EndpointRpcErrorCode.RequestPayloadTooLarge,
                EndpointFailureKind.InvalidInput,
                "The endpoint request payload exceeds the permitted size.",
                error,
                isMutatingOperation),
            IpcErrorCode.ResponsePayloadTooLarge or IpcErrorCode.SerializedEnvelopeTooLarge => Create(
                EndpointRpcErrorCode.ResponsePayloadTooLarge,
                EndpointFailureKind.Generic,
                "The endpoint response payload exceeds the permitted size.",
                error,
                isMutatingOperation),
            IpcErrorCode.UnexpectedPeerRole or
            IpcErrorCode.UnsupportedCapability or
            IpcErrorCode.UiNotRegistered or
            IpcErrorCode.UnauthorizedOperation => Create(
                EndpointRpcErrorCode.AuthorizationFailed,
                EndpointFailureKind.Generic,
                "The endpoint connection is not authorized.",
                error,
                isMutatingOperation),
            IpcErrorCode.UnknownOperation => Create(
                EndpointRpcErrorCode.UnknownOperation,
                EndpointFailureKind.Generic,
                "The endpoint operation is not supported.",
                error,
                isMutatingOperation),
            IpcErrorCode.UiAlreadyRegistered or
            IpcErrorCode.TooManyConnections => Create(
                EndpointRpcErrorCode.Conflict,
                EndpointFailureKind.Generic,
                "An interactive endpoint connection is already active.",
                error,
                isMutatingOperation),
            IpcErrorCode.ServerBusy or
            IpcErrorCode.TooManyRequests or
            IpcErrorCode.AgentStopping or
            IpcErrorCode.AgentUnavailable or
            IpcErrorCode.ConnectionClosed => Create(
                EndpointRpcErrorCode.RuntimeUnavailable,
                EndpointFailureKind.Generic,
                "The endpoint runtime is not available.",
                error,
                isMutatingOperation),
            IpcErrorCode.RequestRejected => Create(
                EndpointRpcErrorCode.OperationRejected,
                EndpointFailureKind.Generic,
                "The endpoint operation was rejected.",
                error,
                isMutatingOperation),
            _ => Create(
                EndpointRpcErrorCode.BackendFailure,
                EndpointFailureKind.Generic,
                "The endpoint transport failed.",
                error,
                isMutatingOperation)
        };

        return new EndpointRpcRemoteException(endpointError);
    }

    private static EndpointRpcError Create(
        EndpointRpcErrorCode errorCode,
        EndpointFailureKind failureKind,
        string safeMessage,
        IpcError error,
        bool isMutatingOperation) =>
        new(
            errorCode,
            failureKind,
            safeMessage,
            error.RequiresProcessRestart && errorCode == EndpointRpcErrorCode.RuntimeUnavailable,
            isMutatingOperation
                ? EndpointMutationOutcome.NotCommitted
                : EndpointMutationOutcome.NotApplicable);
}
