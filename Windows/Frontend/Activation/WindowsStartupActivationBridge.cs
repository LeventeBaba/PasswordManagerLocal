namespace PasswordManagerLocal.Windows.Frontend.Activation;

/// <summary>Accepts early activation without exposing a window before its theme is ready.</summary>
public sealed class WindowsStartupActivationBridge : IWindowsWindowActivationBridge, IDisposable
{
    private readonly IWindowsWindowActivationBridge _inner;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _ready;
    private bool _pending;
    private bool _stopped;

    public WindowsStartupActivationBridge(IWindowsWindowActivationBridge inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public Task<bool> ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_stopped)
                return Task.FromResult(false);
            if (!_ready)
            {
                _pending = true;
                return Task.FromResult(true);
            }
            return ActivateCoreAsync(cancellationToken);
        }
    }

    // Called by the desktop host only after the themed MainWindow has been shown.
    public void MarkPresentationReady()
    {
        lock (_gate)
        {
            if (_stopped)
                return;
            _ready = true;
            if (!_pending)
                return;
            _pending = false;
            _ = ActivateCoreAsync(CancellationToken.None);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped)
                return;
            _stopped = true;
            _pending = false;
        }
        _lifetime.Cancel();
    }

    public void Dispose()
    {
        Stop();
        _lifetime.Dispose();
    }

    private async Task<bool> ActivateCoreAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try { return await _inner.ActivateAsync(linked.Token); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { return false; }
        catch { return false; }
    }
}
