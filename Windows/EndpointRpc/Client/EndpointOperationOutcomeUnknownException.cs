using PasswordManagerLocal.Windows.EndpointRpc.Contracts;

namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

public sealed class EndpointOperationOutcomeUnknownException : Exception
{
    public EndpointOperationOutcomeUnknownException(
        EndpointOperationId operationId,
        bool requiresProcessRestart,
        Exception innerException)
        : base("The endpoint operation may have executed, but its outcome could not be confirmed.", innerException)
    {
        OperationId = operationId;
        RequiresProcessRestart = requiresProcessRestart;
    }

    public EndpointOperationId OperationId { get; }
    public bool RequiresProcessRestart { get; }
    public EndpointMutationOutcome MutationOutcome => EndpointMutationOutcome.OutcomeUnknown;
    public bool BlindRetryIsSafe => false;
    public EndpointRpcError? Error => InnerException switch
    {
        EndpointRpcRemoteException remoteException => remoteException.Error,
        EndpointRpcTransportException { InnerException: EndpointRpcRemoteException remoteException } =>
            remoteException.Error,
        _ => null
    };
}
