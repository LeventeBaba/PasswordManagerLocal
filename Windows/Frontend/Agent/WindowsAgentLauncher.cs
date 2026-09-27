using PasswordManagerLocal.Windows.Ipc.Coordination;
using System.Diagnostics;

namespace PasswordManagerLocal.Windows.Frontend.AgentConnection;

public sealed class WindowsAgentLauncher : IWindowsAgentLauncher
{
    private readonly string _agentExecutablePath;
    private readonly IWindowsAgentProcessLauncher _processLauncher;

    public WindowsAgentLauncher(
        string executableDirectory,
        IWindowsAgentProcessLauncher? processLauncher = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableDirectory);
        _agentExecutablePath = Path.Combine(
            Path.GetFullPath(executableDirectory),
            WindowsExecutableNames.AgentExecutableFileName);
        _processLauncher = processLauncher ?? new WindowsAgentProcessLauncher();
    }

    public Task<bool> LaunchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_agentExecutablePath))
            return Task.FromResult(false);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _agentExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(_agentExecutablePath)!,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(WindowsAgentLaunchArguments.UiRequested);
            return Task.FromResult(_processLauncher.TryStart(startInfo));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}
