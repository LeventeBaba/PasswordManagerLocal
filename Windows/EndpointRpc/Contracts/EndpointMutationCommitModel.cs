namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts;

public enum EndpointMutationCommitModel
{
    NoDurableMutation = 1,
    SingleAtomicCommit = 2,
    CommitThenFollowUp = 3,
    MultiStageRecoverableCommit = 4,
    ExternallyObservableSideEffect = 5
}
