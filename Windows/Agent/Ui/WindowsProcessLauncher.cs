using System.Diagnostics;

namespace PasswordManagerLocal.Windows.Agent.Ui;

public sealed class WindowsProcessLauncher : IProcessLauncher
{
    public bool TryStart(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        return Process.Start(startInfo) is not null;
    }
}
