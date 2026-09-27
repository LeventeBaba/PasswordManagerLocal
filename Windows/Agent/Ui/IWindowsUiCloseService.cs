namespace PasswordManagerLocal.Windows.Agent.Ui;

public interface IWindowsUiCloseService
{
    Task<WindowsUiCloseResult> RequestIntentionalShutdownAsync(
        CancellationToken cancellationToken = default);
}
