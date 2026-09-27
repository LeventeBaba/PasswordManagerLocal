namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public enum WindowsBackgroundSyncConsistency
{
    Disabled = 0,
    Operational = 1,
    Transitioning = 2,
    Degraded = 3,
    Inconsistent = 4,
    Unavailable = 5
}
