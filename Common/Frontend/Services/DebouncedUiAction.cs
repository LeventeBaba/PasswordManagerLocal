using Avalonia.Threading;

namespace PasswordManagerLocal.Common.Frontend.Services;

/// <summary>Coalesces rapid text edits before running collection filters on the UI thread.</summary>
public sealed class DebouncedUiAction(TimeSpan delay) : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _pending;
    private bool _disposed;

    public void Schedule(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        CancellationTokenSource pending;
        lock (_gate)
        {
            if (_disposed)
                return;
            _pending?.Cancel();
            _pending?.Dispose();
            pending = new CancellationTokenSource();
            _pending = pending;
        }
        _ = RunAsync(action, pending, pending.Token);
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }

    private async Task RunAsync(Action action, CancellationTokenSource pending, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                lock (_gate)
                {
                    if (_disposed || token.IsCancellationRequested || !ReferenceEquals(_pending, pending))
                        return;
                    _pending = null;
                }
                action();
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, pending))
                    _pending = null;
            }
            pending.Dispose();
        }
    }
}
