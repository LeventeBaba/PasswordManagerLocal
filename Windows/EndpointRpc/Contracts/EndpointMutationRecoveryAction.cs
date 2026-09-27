namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts;

public enum EndpointMutationRecoveryAction
{
    None = 1,
    InspectAuthoritativeState = 2,
    ReconnectAndInspectSessionState = 3,
    ResumeEnrollmentForSameImmutableTargetOrPerformSignedRemoval = 4
}
