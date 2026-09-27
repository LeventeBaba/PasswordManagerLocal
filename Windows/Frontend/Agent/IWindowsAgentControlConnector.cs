using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Frontend.AgentConnection;

public interface IWindowsAgentControlConnector
{
    Task<WindowsAgentControlConnectionAttempt> TryConnectAndRegisterAsync(
        string pipeName,
        WindowsUiIpcIdentity identity,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken = default);
}
