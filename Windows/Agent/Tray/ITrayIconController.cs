namespace PasswordManagerLocal.Windows.Agent.Tray;

public interface ITrayIconController : IAsyncDisposable
{
    event EventHandler? ContextMenuOpening;
    event EventHandler? OpenRequested;
    event EventHandler? ExitRequested;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SetVisibleAsync(bool isVisible, CancellationToken cancellationToken = default);
    Task UpdateTextAsync(WindowsAgentTrayText text, CancellationToken cancellationToken = default);
    Task ShowExitFailureAsync(string safeMessage, CancellationToken cancellationToken = default);
}
