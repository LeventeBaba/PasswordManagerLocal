namespace PasswordManagerLocal.Windows.Frontend.AgentConnection;

public interface IWindowsAgentLauncher
{
    Task<bool> LaunchAsync(CancellationToken cancellationToken = default);
}
