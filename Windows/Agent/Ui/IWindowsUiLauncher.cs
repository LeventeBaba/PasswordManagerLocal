namespace PasswordManagerLocal.Windows.Agent.Ui;

public interface IWindowsUiLauncher
{
    Task<UiLaunchResult> LaunchAsync(CancellationToken cancellationToken = default);
}
