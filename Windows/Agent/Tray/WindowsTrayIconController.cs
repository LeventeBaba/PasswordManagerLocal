namespace PasswordManagerLocal.Windows.Agent.Tray;

public sealed class WindowsTrayIconController : ITrayIconController
{
    private readonly ITrayIconAdapter _adapter;
    private readonly SemaphoreSlim _visibilityGate = new(1, 1);
    private bool? _isVisible;
    private int _initialized;
    private int _disposed;

    public WindowsTrayIconController(ITrayIconAdapter adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public event EventHandler? ContextMenuOpening;
    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
            throw new InvalidOperationException("The tray icon has already been initialized.");

        _adapter.MouseClicked += HandleMouseClicked;
        _adapter.ContextMenuOpening += HandleContextMenuOpening;
        _adapter.OpenCommandSelected += HandleOpenCommandSelected;
        _adapter.ExitCommandSelected += HandleExitCommandSelected;
        try
        {
            await _adapter.InitializeAsync(cancellationToken);
        }
        catch
        {
            DetachEvents();
            throw;
        }
    }

    public async Task SetVisibleAsync(
        bool isVisible,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _initialized) == 0)
            throw new InvalidOperationException("The tray icon has not been initialized.");

        await _visibilityGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_isVisible == isVisible)
                return;

            await _adapter.SetVisibleAsync(isVisible, cancellationToken);
            _isVisible = isVisible;
        }
        finally
        {
            _visibilityGate.Release();
        }
    }

    public Task UpdateTextAsync(
        WindowsAgentTrayText text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ThrowIfDisposed();
        return _adapter.UpdateTextAsync(text, cancellationToken);
    }

    public Task ShowExitFailureAsync(
        string safeMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        ThrowIfDisposed();
        return _adapter.ShowErrorAsync(safeMessage, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        DetachEvents();
        await _visibilityGate.WaitAsync();
        try
        {
            await _adapter.HideAndDisposeAsync();
        }
        finally
        {
            _visibilityGate.Release();
            GC.SuppressFinalize(this);
        }
    }

    private void HandleContextMenuOpening(object? sender, EventArgs args) =>
        ContextMenuOpening?.Invoke(this, EventArgs.Empty);

    private void HandleMouseClicked(object? sender, TrayIconMouseEventArgs args)
    {
        if (args.Button == TrayIconMouseButton.Left && args.Clicks == 1)
            OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    private void HandleOpenCommandSelected(object? sender, EventArgs args) =>
        OpenRequested?.Invoke(this, EventArgs.Empty);

    private void HandleExitCommandSelected(object? sender, EventArgs args) =>
        ExitRequested?.Invoke(this, EventArgs.Empty);

    private void DetachEvents()
    {
        _adapter.MouseClicked -= HandleMouseClicked;
        _adapter.ContextMenuOpening -= HandleContextMenuOpening;
        _adapter.OpenCommandSelected -= HandleOpenCommandSelected;
        _adapter.ExitCommandSelected -= HandleExitCommandSelected;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WindowsTrayIconController));
    }
}
