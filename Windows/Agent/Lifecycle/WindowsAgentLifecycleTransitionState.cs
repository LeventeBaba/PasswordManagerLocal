namespace PasswordManagerLocal.Windows.Agent.Lifecycle;

public enum WindowsAgentLifecycleTransitionState
{
    Idle = 0,
    ChangingBackgroundSync = 1,
    ResettingDatabase = 2,
    ShuttingDown = 3
}
