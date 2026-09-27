namespace PasswordManagerLocal.Windows.Agent.Backend;

public enum WindowsAgentBackendOwnerState
{
    NotCreated = 0,
    Ready = 1,
    Interactive = 2,
    Resetting = 3,
    RestartRequired = 4,
    Stopping = 5,
    Stopped = 6,
    Failed = 7
}
