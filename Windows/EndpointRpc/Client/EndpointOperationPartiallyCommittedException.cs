using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Metadata;

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
        var descriptor = EndpointOperationManifest.Get(operationId);
        OperationId = operationId;
        Recovery = recovery;
        RequiresProcessRestart = requiresProcessRestart;
        ReplaySafety = descriptor.ReplaySafety;
        RecoveryAction = descriptor.RecoveryAction;
        AuthoritativeReadBackOperationId = descriptor.AuthoritativeReadBackOperationId;
    }

    public EndpointOperationId OperationId { get; }
    public bool RequiresRecovery => true;
    public bool RequiresProcessRestart { get; }
    public EndpointRecoveryMetadata? Recovery { get; }
    public EndpointMutationReplaySafety ReplaySafety { get; }
    public EndpointMutationRecoveryAction RecoveryAction { get; }
    public EndpointOperationId? AuthoritativeReadBackOperationId { get; }
    public EndpointMutationOutcome MutationOutcome => EndpointMutationOutcome.PartiallyCommittedRecoveryRequired;
    public bool BlindRetryIsSafe => false;
}
