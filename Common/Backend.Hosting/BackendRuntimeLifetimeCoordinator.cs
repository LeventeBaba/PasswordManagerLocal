using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;

namespace PasswordManagerLocal.Common.Backend.Hosting;

public sealed class BackendRuntimeLifetimeCoordinator : IBackendRuntimeLifetimeCoordinator
{
    private readonly IBackendRuntime _runtime;
    private readonly SemaphoreSlim _transitionLock = new(1, 1);
    private readonly BackendExecutionProfileProvider _executionProfileProvider;
    private readonly object _stateLock = new();
    private int _interactiveUiCount;
    private int _backgroundSyncCount;

    public BackendRuntimeLifetimeCoordinator(IBackendRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _executionProfileProvider = new BackendExecutionProfileProvider(this);
        if (runtime is IBackendExecutionProfileProviderSink sink)
            sink.SetExecutionProfileProvider(_executionProfileProvider);
    }

    public event EventHandler? ActiveReasonsChanged;

    public IBackendExecutionProfileProvider ExecutionProfileProvider => _executionProfileProvider;

    public BackendLifetimeReason ActiveReasons
    {
        get
        {
            lock (_stateLock)
                return GetActiveReasonsLocked();
        }
    }

    public async Task<IBackendRuntimeLease> AcquireAsync(
        BackendLifetimeReason reason,
        CancellationToken cancellationToken = default)
    {
        ValidateReason(reason);
        await _transitionLock.WaitAsync(cancellationToken);
        var reasonsChanged = false;
        var reasonAdded = false;
        var firstLease = false;
        var runtimeStarted = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            firstLease = GetTotalLeaseCount() == 0;
            reasonsChanged = Increment(reason);
            reasonAdded = true;

            if (firstLease)
            {
                ((IBackendExecutionProfileProviderLifecycle)_executionProfileProvider).InitializeCurrent();
                await _runtime.EnsureStartedAsync(cancellationToken);
                runtimeStarted = true;
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new BackendRuntimeLease(this, reason);
        }
        catch
        {
            if (reasonAdded)
            {
                Decrement(reason);
                ((IBackendExecutionProfileProviderLifecycle)_executionProfileProvider).InitializeCurrent();
            }

            if (runtimeStarted && GetTotalLeaseCount() == 0)
                await _runtime.StopAsync(CancellationToken.None);

            reasonsChanged = false;
            throw;
        }
        finally
        {
            _transitionLock.Release();
            if (reasonsChanged)
                PublishActiveReasonsChanged();
        }
    }

    public async Task<IBackendRuntimeLease> ResetDatabaseAndAcquireAsync(
        BackendLifetimeReason reason,
        CancellationToken cancellationToken = default)
    {
        ValidateReason(reason);
        await _transitionLock.WaitAsync(cancellationToken);
        var reasonsChanged = false;
        var reasonAdded = false;
        var runtimeRestarted = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetTotalLeaseCount() != 0)
            {
                throw new InvalidOperationException(
                    "The database cannot be reset while runtime leases are active.");
            }

            reasonsChanged = Increment(reason);
            reasonAdded = true;
            ((IBackendExecutionProfileProviderLifecycle)_executionProfileProvider).InitializeCurrent();

            await _runtime.ResetDatabaseAndRestartAsync(cancellationToken);
            runtimeRestarted = true;
            cancellationToken.ThrowIfCancellationRequested();
            return new BackendRuntimeLease(this, reason);
        }
        catch
        {
            if (reasonAdded)
            {
                Decrement(reason);
                ((IBackendExecutionProfileProviderLifecycle)_executionProfileProvider).InitializeCurrent();
            }

            if (runtimeRestarted && GetTotalLeaseCount() == 0)
                await _runtime.StopAsync(CancellationToken.None);

            reasonsChanged = false;
            throw;
        }
        finally
        {
            _transitionLock.Release();
            if (reasonsChanged)
                PublishActiveReasonsChanged();
        }
    }

    public async Task RecoverRuntimeAsync(CancellationToken cancellationToken = default)
    {
        await _transitionLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _runtime.StopAsync(CancellationToken.None);

            if (GetTotalLeaseCount() > 0)
                await _runtime.EnsureStartedAsync(CancellationToken.None);
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    internal async ValueTask ReleaseAsync(BackendLifetimeReason reason)
    {
        await _transitionLock.WaitAsync(CancellationToken.None);
        var reasonsChanged = false;
        try
        {
            reasonsChanged = Decrement(reason);
            if (GetTotalLeaseCount() == 0)
                await _runtime.StopAsync(CancellationToken.None);
        }
        finally
        {
            _transitionLock.Release();
            if (reasonsChanged)
                PublishActiveReasonsChanged();
        }
    }

    private int GetTotalLeaseCount()
    {
        lock (_stateLock)
            return _interactiveUiCount + _backgroundSyncCount;
    }

    private bool Increment(BackendLifetimeReason reason)
    {
        lock (_stateLock)
        {
            var previous = GetActiveReasonsLocked();
            if (reason == BackendLifetimeReason.InteractiveUi)
                _interactiveUiCount++;
            else
                _backgroundSyncCount++;
            return previous != GetActiveReasonsLocked();
        }
    }

    private bool Decrement(BackendLifetimeReason reason)
    {
        lock (_stateLock)
        {
            var previous = GetActiveReasonsLocked();
            if (reason == BackendLifetimeReason.InteractiveUi)
            {
                if (_interactiveUiCount == 0)
                    throw new InvalidOperationException("No interactive UI runtime lease is active.");

                _interactiveUiCount--;
            }
            else
            {
                if (_backgroundSyncCount == 0)
                    throw new InvalidOperationException("No background synchronization runtime lease is active.");

                _backgroundSyncCount--;
            }
            return previous != GetActiveReasonsLocked();
        }
    }

    private BackendLifetimeReason GetActiveReasonsLocked()
    {
        var reasons = BackendLifetimeReason.None;
        if (_interactiveUiCount > 0)
            reasons |= BackendLifetimeReason.InteractiveUi;
        if (_backgroundSyncCount > 0)
            reasons |= BackendLifetimeReason.BackgroundSync;
        return reasons;
    }

    private void PublishActiveReasonsChanged()
    {
        var handlers = ActiveReasonsChanged;
        if (handlers is null)
            return;

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); } catch { }
        }
    }

    private static void ValidateReason(BackendLifetimeReason reason)
    {
        if (reason is not BackendLifetimeReason.InteractiveUi and not BackendLifetimeReason.BackgroundSync)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                reason,
                "A runtime lease must represent exactly one lifetime reason.");
        }
    }
}
