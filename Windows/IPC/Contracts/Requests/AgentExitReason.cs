namespace PasswordManagerLocal.Windows.Ipc.Contracts.Requests;

public enum AgentExitReason
{
    UserRequested = 1,
    ApplicationUpdate = 2,
    ProcessRestartRequired = 3
}
