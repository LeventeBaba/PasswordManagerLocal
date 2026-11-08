using PasswordManagerLocal.Windows.Ipc.Coordination;
using System.Diagnostics;

namespace PasswordManagerLocal.Windows.Frontend.AgentConnection;

public sealed class WindowsAgentLauncher : IWindowsAgentLauncher
{
    private readonly string _agentExecutablePath;
    private readonly IWindowsAgentProcessLauncher _processLauncher;
    private readonly bool _unifiedExecutable;

    public WindowsAgentLauncher(
        string executableDirectory,
        IWindowsAgentProcessLauncher? processLauncher = null,
        bool unifiedExecutable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableDirectory);
        var executableName = unifiedExecutable
            ? WindowsExecutableNames.UiExecutableFileName
            : WindowsExecutableNames.AgentExecutableFileName;
        _agentExecutablePath = Path.Combine(Path.GetFullPath(executableDirectory), executableName);
        _unifiedExecutable = unifiedExecutable;
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
            if (_unifiedExecutable)
                startInfo.ArgumentList.Add(WindowsAgentLaunchArguments.AgentRole);
            startInfo.ArgumentList.Add(WindowsAgentLaunchArguments.UiRequested);
            return Task.FromResult(_processLauncher.TryStart(startInfo));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}
