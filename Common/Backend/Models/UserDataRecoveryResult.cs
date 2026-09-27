namespace PasswordManagerLocal.Common.Backend.Models;

public sealed record UserDataRecoveryResult(
    UserDataRecoveryState State,
    int HealthyCandidateCount = 0,
    long? RecoveryRevision = null,
    string? DiagnosticCode = null)
{
    public bool Recovered => State == UserDataRecoveryState.Recovered;
}
