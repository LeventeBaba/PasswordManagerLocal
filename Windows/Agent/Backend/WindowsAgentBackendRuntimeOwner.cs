using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Windows.Backend;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;

namespace PasswordManagerLocal.Windows.Agent.Backend;

public sealed class WindowsAgentBackendRuntimeOwner : IWindowsAgentBackendRuntimeOwner
{
    private readonly Func<BackendRuntimeComposition> _compositionFactory;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly object _snapshotGate = new();
    private BackendRuntimeComposition? _composition;
    private int _activeBindings;
    private bool _disposed;
    private int _disposeStarted;
    private WindowsAgentBackendOwnerSnapshot _snapshot;

    public WindowsAgentBackendRuntimeOwner(
        Func<BackendRuntimeComposition>? compositionFactory = null)
    {
        _compositionFactory = compositionFactory ?? WindowsBackendRuntimeFactory.Create;
        _snapshot = CreateInitialSnapshot();
    }

    public WindowsAgentBackendOwnerSnapshot Snapshot
    {
        get
        {
            lock (_snapshotGate)
                return _snapshot;
        }
    }

    public event EventHandler? StateChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (Snapshot.State is WindowsAgentBackendOwnerState.Stopping or
                WindowsAgentBackendOwnerState.Stopped or
                WindowsAgentBackendOwnerState.RestartRequired)
            {
                throw new InvalidOperationException("The agent backend owner cannot be started in its current state.");
            }
            if (_composition is not null)
                return;

            try
            {
                var composition = _compositionFactory()
                    ?? throw new InvalidOperationException("The Windows backend composition factory returned null.");
                _composition = composition;
                composition.Runtime.StateChanged += HandleRuntimeStateChanged;
                composition.Runtime.SyncStateChanged += HandleSyncStateChanged;
                composition.LifetimeCoordinator.ActiveReasonsChanged += HandleActiveReasonsChanged;
                Publish(WindowsAgentBackendOwnerState.Ready, null, requiresRestart: false, isResetting: false);
            }
            catch (Exception exception)
            {
                Publish(WindowsAgentBackendOwnerState.Failed, exception, requiresRestart: false, isResetting: false);
                throw;
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task<IBackendRuntimeLease> AcquireBackgroundSyncLeaseAsync(
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var composition = GetUsableComposition();
            try
            {
                return await composition.LifetimeCoordinator.AcquireAsync(
                    BackendLifetimeReason.BackgroundSync,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                if (composition.Runtime.Snapshot.State == BackendRuntimeState.Failed)
                    Publish(WindowsAgentBackendOwnerState.Failed, exception, requiresRestart: false, isResetting: false);
                throw;
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task<AgentInteractiveBackendBinding> OpenInteractiveBindingAsync(
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var composition = GetUsableComposition();
            if (_activeBindings != 0)
                throw new InvalidOperationException("An interactive endpoint session is already active.");

            IBackendRuntimeLease? lease = null;
            try
            {
                lease = await composition.LifetimeCoordinator.AcquireAsync(
                    BackendLifetimeReason.InteractiveUi,
                    cancellationToken);
                var session = await composition.Runtime.OpenInteractiveSessionAsync(cancellationToken);
                _activeBindings = 1;
                Publish(WindowsAgentBackendOwnerState.Interactive, null, requiresRestart: false, isResetting: false);
                return new AgentInteractiveBackendBinding(session, lease, HandleBindingReleasedAsync);
            }
            catch (Exception exception)
            {
                var leaseCleanupFailed = false;
                if (lease is not null)
                {
                    try
                    {
                        await lease.DisposeAsync();
                    }
                    catch (Exception cleanupException)
                    {
                        leaseCleanupFailed = true;
                        exception = new AggregateException(exception, cleanupException);
                    }
                }

                if (leaseCleanupFailed || composition.Runtime.InteractiveSessionSnapshot.RequiresRecovery)
                {
                    RequireProcessRestart(exception);
                }
                else if (composition.Runtime.Snapshot.State == BackendRuntimeState.Failed)
                {
                    Publish(WindowsAgentBackendOwnerState.Failed, exception, requiresRestart: false, isResetting: false);
                }
                else
                {
                    Publish(WindowsAgentBackendOwnerState.Ready, null, requiresRestart: false, isResetting: false);
                }
                throw;
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task ResetDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var snapshot = Snapshot;
            if (snapshot.RequiresProcessRestart || snapshot.IsResetting ||
                snapshot.State is WindowsAgentBackendOwnerState.Stopping or WindowsAgentBackendOwnerState.Stopped)
            {
                throw new InvalidOperationException("The agent backend runtime cannot be reset in its current state.");
            }

            var composition = _composition
                ?? throw new InvalidOperationException("The agent backend runtime has not been created.");
            if (_activeBindings != 0 || composition.LifetimeCoordinator.ActiveReasons != BackendLifetimeReason.None)
                throw new InvalidOperationException("The database cannot be reset while an endpoint session is active.");

            Publish(WindowsAgentBackendOwnerState.Resetting, null, requiresRestart: false, isResetting: true);
            try
            {
                await composition.Runtime.ResetDatabaseAndRestartAsync(cancellationToken);
                await composition.Runtime.StopAsync(CancellationToken.None);
                Publish(WindowsAgentBackendOwnerState.Ready, null, requiresRestart: false, isResetting: false);
            }
            catch (Exception exception)
            {
                RequireProcessRestart(exception);
                throw;
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed)
                return;
            if (_composition is null)
            {
                Publish(WindowsAgentBackendOwnerState.Stopped, null, Snapshot.RequiresProcessRestart, false);
                return;
            }
            if (_activeBindings != 0)
                throw new InvalidOperationException("Interactive endpoint sessions must close before the runtime stops.");

            Publish(WindowsAgentBackendOwnerState.Stopping, null, Snapshot.RequiresProcessRestart, false);
            try
            {
                await _composition.Runtime.StopAsync(cancellationToken);
                Publish(WindowsAgentBackendOwnerState.Stopped, null, Snapshot.RequiresProcessRestart, false);
            }
            catch (Exception exception)
            {
                RequireProcessRestart(exception);
                throw;
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public void RequireProcessRestart(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Publish(WindowsAgentBackendOwnerState.RestartRequired, failure, requiresRestart: true, isResetting: false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        await _transitionGate.WaitAsync(CancellationToken.None);
        try
        {
            if (_disposed)
                return;
            _disposed = true;

            var composition = _composition;
            _composition = null;
            if (composition is not null)
            {
                composition.Runtime.StateChanged -= HandleRuntimeStateChanged;
                composition.Runtime.SyncStateChanged -= HandleSyncStateChanged;
                composition.LifetimeCoordinator.ActiveReasonsChanged -= HandleActiveReasonsChanged;
                await composition.Runtime.DisposeAsync();
            }

            Publish(WindowsAgentBackendOwnerState.Stopped, null, Snapshot.RequiresProcessRestart, false);
        }
        finally
        {
            _transitionGate.Release();
            _transitionGate.Dispose();
        }
        StateChanged = null;
        GC.SuppressFinalize(this);
    }

    private BackendRuntimeComposition GetUsableComposition()
    {
        var snapshot = Snapshot;
        if (snapshot.RequiresProcessRestart || snapshot.IsResetting ||
            snapshot.State is WindowsAgentBackendOwnerState.Stopping or
                WindowsAgentBackendOwnerState.Stopped or
                WindowsAgentBackendOwnerState.Failed)
        {
            throw new InvalidOperationException("The agent backend runtime is unavailable.");
        }

        return _composition
            ?? throw new InvalidOperationException("The agent backend runtime has not been created.");
    }

    private ValueTask HandleBindingReleasedAsync()
    {
        if (Interlocked.Exchange(ref _activeBindings, 0) != 1)
            return ValueTask.CompletedTask;

        if (!Snapshot.RequiresProcessRestart && !Snapshot.IsResetting)
            Publish(WindowsAgentBackendOwnerState.Ready, null, requiresRestart: false, isResetting: false);
        return ValueTask.CompletedTask;
    }

    private void HandleRuntimeStateChanged(object? sender, BackendRuntimeStateChangedEventArgs args) =>
        RefreshFromRuntime();

    private void HandleActiveReasonsChanged(object? sender, EventArgs args) =>
        RefreshFromRuntime();

    private void HandleSyncStateChanged(object? sender, SyncRuntimeStateChangedEventArgs args) =>
        RefreshFromRuntime();

    private void RefreshFromRuntime()
    {
        var current = Snapshot;
        Publish(current.State, current.Failure, current.RequiresProcessRestart, current.IsResetting);
    }

    private void Publish(
        WindowsAgentBackendOwnerState state,
        Exception? failure,
        bool requiresRestart,
        bool isResetting)
    {
        BackendRuntimeSnapshot runtime;
        InteractiveSessionLifecycleSnapshot interactive;
        SyncRuntimeSnapshot sync;
        BackendLifetimeReason reasons;
        var composition = _composition;
        if (composition is null)
        {
            runtime = new BackendRuntimeSnapshot(
                BackendRuntimeState.NotStarted,
                BackendRuntimeFailureKind.None,
                null,
                DateTimeOffset.UtcNow);
            interactive = new InteractiveSessionLifecycleSnapshot(
                InteractiveSessionLifecycleState.None,
                null,
                DateTimeOffset.UtcNow);
            sync = new SyncRuntimeSnapshot(SyncRuntimeState.Disabled, null);
            reasons = BackendLifetimeReason.None;
        }
        else
        {
            runtime = composition.Runtime.Snapshot;
            interactive = composition.Runtime.InteractiveSessionSnapshot;
            sync = composition.Runtime.SyncSnapshot;
            reasons = composition.LifetimeCoordinator.ActiveReasons;
        }

        lock (_snapshotGate)
        {
            _snapshot = new WindowsAgentBackendOwnerSnapshot(
                state,
                runtime,
                interactive,
                sync,
                reasons,
                requiresRestart,
                isResetting,
                failure,
                DateTimeOffset.UtcNow);
        }

        var handlers = StateChanged;
        if (handlers is null)
            return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); } catch { }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WindowsAgentBackendRuntimeOwner));
    }

    private WindowsAgentBackendOwnerSnapshot CreateInitialSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        return new WindowsAgentBackendOwnerSnapshot(
            WindowsAgentBackendOwnerState.NotCreated,
            new BackendRuntimeSnapshot(
                BackendRuntimeState.NotStarted,
                BackendRuntimeFailureKind.None,
                null,
                now),
            new InteractiveSessionLifecycleSnapshot(
                InteractiveSessionLifecycleState.None,
                null,
                now),
            new SyncRuntimeSnapshot(SyncRuntimeState.Disabled, null),
            BackendLifetimeReason.None,
            RequiresProcessRestart: false,
            IsResetting: false,
            Failure: null,
            ChangedAtUtc: now);
    }
}
