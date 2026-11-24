namespace PasswordManagerLocal.Windows.Ipc.Contracts.Status;

public enum WindowsBackgroundSyncConsistency
{
    Disabled = 0,
    Operational = 1,
    Transitioning = 2,
    Degraded = 3,
    Inconsistent = 4,
    Unavailable = 5,
    EnabledIdle = 6,
    Starting = 7,
    DegradedRetrying = 8,
    RestartRequired = 9
}
