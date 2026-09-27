using System.Diagnostics;

namespace PasswordManagerLocal.Windows.Agent.Ui;

public interface IProcessLauncher
{
    bool TryStart(ProcessStartInfo startInfo);
}
