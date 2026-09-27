namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts;

public enum EndpointMutationReplaySafety
{
    NotApplicable = 1,
    AuthoritativeReadBackRequired = 2,
    RecoveryForSameImmutableTargetOnly = 3
}
