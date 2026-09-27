namespace PasswordManagerLocal.Common.Backend.Models;

public enum UserDataRecoveryTrigger : byte
{
    Login = 0,
    RememberMeStartup = 1,
    HealthyCandidateReceived = 2,
    PublisherHealthFailure = 3,
    StartupRecoveryScan = 4,
    ManualRetry = 5,
    PeriodicMaintenance = 6
}
