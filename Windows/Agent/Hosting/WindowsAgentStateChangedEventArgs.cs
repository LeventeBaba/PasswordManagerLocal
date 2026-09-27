using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Agent.Hosting;

public sealed class WindowsAgentStateChangedEventArgs : EventArgs
{
    public WindowsAgentStateChangedEventArgs(AgentState previous, AgentState current)
    {
        Previous = previous;
        Current = current;
    }

    public AgentState Previous { get; }
    public AgentState Current { get; }
}
