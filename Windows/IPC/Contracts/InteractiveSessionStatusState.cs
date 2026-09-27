namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public enum InteractiveSessionStatusState
{
    None = 0,
    Opening = 1,
    Active = 2,
    Closing = 3,
    CleanupFailed = 4
}
