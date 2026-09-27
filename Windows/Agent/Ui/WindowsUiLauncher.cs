using PasswordManagerLocal.Windows.Agent.Localization;
using PasswordManagerLocal.Windows.Ipc.Coordination;
using System.Diagnostics;

namespace PasswordManagerLocal.Windows.Agent.Ui;

public sealed class WindowsUiLauncher : IWindowsUiLauncher
{
    private readonly string _uiExecutablePath;
    private readonly IProcessLauncher _processLauncher;
    private readonly IAgentLocalizer _localizer;

    public WindowsUiLauncher(
        string executableDirectory,
        IAgentLocalizer localizer,
        IProcessLauncher? processLauncher = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableDirectory);
        _uiExecutablePath = Path.Combine(
            Path.GetFullPath(executableDirectory),
            WindowsExecutableNames.UiExecutableFileName);
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _processLauncher = processLauncher ?? new WindowsProcessLauncher();
    }

    public Task<UiLaunchResult> LaunchAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_uiExecutablePath))
        {
            return Task.FromResult(new UiLaunchResult(
                UiLaunchResultKind.ExecutableNotFound,
                _localizer.GetString(AgentLocalizationKeys.UiExecutableNotFound)));
        }

        try
        {
            var started = _processLauncher.TryStart(new ProcessStartInfo
            {
                FileName = _uiExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(_uiExecutablePath)!,
                UseShellExecute = true
            });
            return Task.FromResult(started
                ? new UiLaunchResult(
                    UiLaunchResultKind.LaunchRequested,
                    _localizer.GetString(AgentLocalizationKeys.UiLaunchRequested))
                : new UiLaunchResult(
                    UiLaunchResultKind.LaunchFailed,
                    _localizer.GetString(AgentLocalizationKeys.UiLaunchFailed)));
        }
        catch
        {
            return Task.FromResult(new UiLaunchResult(
                UiLaunchResultKind.LaunchFailed,
                _localizer.GetString(AgentLocalizationKeys.UiLaunchFailed)));
        }
    }
}
