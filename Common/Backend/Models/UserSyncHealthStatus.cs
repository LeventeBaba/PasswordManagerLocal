namespace PasswordManagerLocal.Common.Backend.Models;

public enum UserSyncHealthStatus : byte
{
    Healthy = 0,
    Suspect = 1,
    Isolated = 2,
    AwaitingEvidence = 3,
    Recovering = 4,
    Recovered = 5,
    Superseded = 6,
    TerminalConflict = 7
}
