namespace PasswordManagerLocal.Windows.Agent.Hosting;

public enum WindowsAgentShutdownReason
{
    UserRequestedExit = 1,
    RestartRequired = 2,
    StartupFailure = 3,
    FatalLifecycleFailure = 4,
    ApplicationExit = 5,
    NoUiAndBackgroundDisabled = 6
}
