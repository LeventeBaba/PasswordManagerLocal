namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public enum AgentExitReason
{
    UserRequested = 1,
    ApplicationUpdate = 2,
    ProcessRestartRequired = 3
}
