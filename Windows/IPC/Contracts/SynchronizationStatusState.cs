namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public enum SynchronizationStatusState
{
    Disabled = 0,
    Starting = 1,
    Running = 2,
    Stopping = 3,
    Degraded = 4,
    Unavailable = 5
}
