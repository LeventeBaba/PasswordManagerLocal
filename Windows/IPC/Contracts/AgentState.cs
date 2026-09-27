namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public enum AgentState
{
    NotStarted = 0,
    Starting = 1,
    Running = 2,
    Stopping = 3,
    Stopped = 4,
    Failed = 5
}
