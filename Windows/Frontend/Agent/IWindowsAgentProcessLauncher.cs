using System.Diagnostics;

namespace PasswordManagerLocal.Windows.Frontend.AgentConnection;

public interface IWindowsAgentProcessLauncher
{
    bool TryStart(ProcessStartInfo startInfo);
}
