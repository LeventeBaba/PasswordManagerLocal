using PasswordManagerLocal.Windows.Agent.Tray;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeTrayIconAdapter : ITrayIconAdapter
{
    public event EventHandler<TrayIconMouseEventArgs>? MouseClicked;
    public event EventHandler? OpenCommandSelected;
    public event EventHandler? ExitCommandSelected;
    public event EventHandler? ContextMenuOpening;

    public int InitializeCount { get; private set; }
    public int HideAndDisposeCount { get; private set; }
    public int SetVisibleCount { get; private set; }
    public bool? LastVisible { get; private set; }
    public int ShowErrorCount { get; private set; }
    public string? LastErrorMessage { get; private set; }
    public int UpdateTextCount { get; private set; }
    public WindowsAgentTrayText? LastText { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InitializeCount++;
        return Task.CompletedTask;
    }

    public Task SetVisibleAsync(
        bool isVisible,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetVisibleCount++;
        LastVisible = isVisible;
        return Task.CompletedTask;
    }


    public Task UpdateTextAsync(
        WindowsAgentTrayText text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastText = text ?? throw new ArgumentNullException(nameof(text));
        UpdateTextCount++;
        return Task.CompletedTask;
    }

    public Task ShowErrorAsync(
        string safeMessage,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ShowErrorCount++;
        LastErrorMessage = safeMessage;
        return Task.CompletedTask;
    }

    public Task HideAndDisposeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HideAndDisposeCount++;
        return Task.CompletedTask;
    }

    public void RaiseMouse(TrayIconMouseButton button, int clicks = 1) =>
        MouseClicked?.Invoke(this, new TrayIconMouseEventArgs(button, clicks));
    public void RaiseContextMenuOpening() => ContextMenuOpening?.Invoke(this, EventArgs.Empty);
    public void RaiseOpenCommand() => OpenCommandSelected?.Invoke(this, EventArgs.Empty);
    public void RaiseExitCommand() => ExitCommandSelected?.Invoke(this, EventArgs.Empty);
}
