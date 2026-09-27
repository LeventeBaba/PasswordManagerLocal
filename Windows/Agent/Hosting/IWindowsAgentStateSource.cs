using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Agent.Hosting;

public interface IWindowsAgentStateSource
{
    AgentState State { get; }
    DateTimeOffset? StartedAtUtc { get; }
    IpcFailureDto? LastFailure { get; }
}
