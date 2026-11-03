using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Windows.Frontend.AgentConnection;

namespace PasswordManagerLocal.Windows.Frontend.Settings;

/// <summary>Persists no state: the Agent rereads the already committed preferences file.</summary>
public sealed class WindowsAgentApplicationPreferencesNotifier : IApplicationPreferencesChangeNotifier, IDisposable
{
    private readonly IWindowsAgentControlConnection _agentConnection;
    private readonly IBackendRuntimeClient? _runtime;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private Task? _flush;
    private bool _pending;
    private bool _disposed;

    public WindowsAgentApplicationPreferencesNotifier(
        IWindowsAgentControlConnection agentConnection,
        IBackendRuntimeClient? runtime = null)
    {
        _agentConnection = agentConnection ?? throw new ArgumentNullException(nameof(agentConnection));
        _runtime = runtime;
        if (_runtime is not null)
            _runtime.StateChanged += HandleRuntimeStateChanged;
    }

    public Task NotifyLanguagePersistedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Preserve the direct notification contract for callers without startup ownership.
        if (_runtime is null)
            return ReloadAsync(cancellationToken);
        lock (_gate)
        {
            if (_disposed)
                return Task.CompletedTask;
            _pending = true;
            StartFlushLocked();
        }
        // Local persistence and local appearance have already succeeded. Starting or
        // reconnecting an Agent must not hold the preferences write lock or the UI.
        return Task.CompletedTask;
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        if (!await _agentConnection.ReloadApplicationPreferencesAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The Windows Agent rejected the application-preference reload request.");
    }

    private void HandleRuntimeStateChanged(object? sender, BackendRuntimeStateChangedEventArgs args)
    {
        lock (_gate)
            StartFlushLocked();
    }

    private void StartFlushLocked()
    {
        if (_disposed || !_pending || _flush is not null || _runtime?.Snapshot.IsReady != true)
            return;
        _flush = Task.Run(FlushAsync);
    }

    private async Task FlushAsync()
    {
        var token = _lifetime.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                lock (_gate)
                {
                    if (_disposed || !_pending || _runtime?.Snapshot.IsReady != true)
                        return;
                    _pending = false;
                }
                try { await ReloadAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch
                {
                    lock (_gate)
                        _pending = true;
                    // Metadata-only notification is idempotent; retry a transient
                    // control failure without launching or connecting an Agent here.
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            lock (_gate)
            {
                _flush = null;
                StartFlushLocked();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _pending = false;
            if (_runtime is not null)
                _runtime.StateChanged -= HandleRuntimeStateChanged;
        }
        _lifetime.Cancel();
        // Flush owns any in-flight cancellation registrations. Dispose the source
        // after the worker has retired rather than blocking desktop shutdown.
        _ = DisposeLifetimeAsync();
    }

    private async Task DisposeLifetimeAsync()
    {
        Task? flush;
        lock (_gate)
            flush = _flush;
        try { if (flush is not null) await flush.ConfigureAwait(false); }
        catch { }
        finally { _lifetime.Dispose(); }
    }
}
