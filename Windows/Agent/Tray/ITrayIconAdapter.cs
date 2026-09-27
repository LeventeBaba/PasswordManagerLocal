namespace PasswordManagerLocal.Windows.Agent.Tray;

public interface ITrayIconAdapter
{
    event EventHandler<TrayIconMouseEventArgs>? MouseClicked;
    event EventHandler? ContextMenuOpening;
    event EventHandler? OpenCommandSelected;
    event EventHandler? ExitCommandSelected;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SetVisibleAsync(bool isVisible, CancellationToken cancellationToken = default);
    Task UpdateTextAsync(WindowsAgentTrayText text, CancellationToken cancellationToken = default);
    Task ShowErrorAsync(string safeMessage, CancellationToken cancellationToken = default);
    Task HideAndDisposeAsync(CancellationToken cancellationToken = default);
}
