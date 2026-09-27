namespace PasswordManagerLocal.Windows.Frontend.AgentConnection;

public sealed record WindowsAgentControlConnectionAttempt(
    IWindowsAgentRegisteredConnection? Connection,
    int? AgentProcessId)
{
    public bool IsConnected => Connection?.IsConnected == true;
}
