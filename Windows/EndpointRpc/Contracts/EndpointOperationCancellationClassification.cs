namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts;

public enum EndpointOperationCancellationClassification
{
    ReadOnlySafelyCancellable = 1,
    IdempotentMutation = 2,
    SideEffectingUnknownOutcome = 3,
    CriticalAdmittedOperation = 4
}
