namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts;

public enum EndpointMutationOutcome
{
    NotApplicable = 1,
    NotCommitted = 2,
    Committed = 3,
    PartiallyCommittedRecoveryRequired = 4,
    OutcomeUnknown = 5
}
