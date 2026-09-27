namespace PasswordManagerLocal.Windows.Agent.Hosting;

public enum WindowsAgentExitCode
{
    Success = 0,
    AlreadyRunning = 10,
    OwnershipFailure = 11,
    ShellFailure = 12
}
