using PasswordManagerLocal.Windows.EndpointRpc.Contracts;

namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

public sealed class EndpointOperationPartiallyCommittedException : Exception
{
    public EndpointOperationPartiallyCommittedException(
        EndpointOperationId operationId,
        EndpointRecoveryMetadata? recovery,
        bool requiresProcessRestart,
        Exception innerException)
        : base("The endpoint operation committed authoritative state, but required follow-up work did not complete.", innerException)
    {
        OperationId = operationId;
        Recovery = recovery;
        RequiresProcessRestart = requiresProcessRestart;
    }

    public EndpointOperationId OperationId { get; }
    public bool RequiresRecovery => true;
    public bool RequiresProcessRestart { get; }
    public EndpointRecoveryMetadata? Recovery { get; }
    public EndpointMutationOutcome MutationOutcome => EndpointMutationOutcome.PartiallyCommittedRecoveryRequired;
    public bool BlindRetryIsSafe => false;
}
