using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;

namespace PasswordManagerLocal.Common.Backend.Hosting;

public sealed class InProcessFrontendBackendClient : IFrontendBackendClient<IEndpoints>
{
    private readonly IBackendRuntime _backendRuntime;
    private readonly IBackendRuntimeLifetimeCoordinator _lifetimeCoordinator;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private IBackendRuntimeLease? _runtimeLease;
    private IInteractiveBackendSession? _interactiveSession;
    private volatile bool _disposed;

    public InProcessFrontendBackendClient(
        IBackendRuntime backendRuntime,
        IBackendRuntimeLifetimeCoordinator lifetimeCoordinator)
    {
        _backendRuntime = backendRuntime ?? throw new ArgumentNullException(nameof(backendRuntime));
        _lifetimeCoordinator = lifetimeCoordinator
            ?? throw new ArgumentNullException(nameof(lifetimeCoordinator));
        _backendRuntime.StateChanged += HandleRuntimeStateChanged;
    }

    public BackendRuntimeSnapshot Snapshot
    {
        get
        {
            ThrowIfDisposed();
            return _backendRuntime.Snapshot;
        }
    }

    public event EventHandler<BackendRuntimeStateChangedEventArgs>? StateChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();

            if (_interactiveSession is not null)
                return;

            var lease = await _lifetimeCoordinator.AcquireAsync(
                BackendLifetimeReason.InteractiveUi,
                cancellationToken);

            try
            {
                var session = await _backendRuntime.OpenInteractiveSessionAsync(cancellationToken);
                _runtimeLease = lease;
                _interactiveSession = session;
            }
            catch (Exception connectionException)
            {
                Exception failure = connectionException;

                if (_backendRuntime.InteractiveSessionSnapshot.RequiresRecovery)
                {
                    try
                    {
                        await _lifetimeCoordinator.RecoverRuntimeAsync(CancellationToken.None);
                    }
                    catch (Exception recoveryException)
                    {
                        failure = new AggregateException(failure, recoveryException);
                    }
                }

                try
                {
                    await lease.DisposeAsync();
                }
                catch (Exception cleanupException)
                {
                    failure = new AggregateException(failure, cleanupException);
                }

                throw failure;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_interactiveSession is null)
                throw new InvalidOperationException("The interactive backend client is not connected.");

            await _backendRuntime.WaitUntilReadyAsync(cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task ResetDatabaseAndRestartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await CloseConnectionAsync();

            var lease = await _lifetimeCoordinator.ResetDatabaseAndAcquireAsync(
                BackendLifetimeReason.InteractiveUi,
                cancellationToken);

            try
            {
                var session = await _backendRuntime.OpenInteractiveSessionAsync(cancellationToken);
                _runtimeLease = lease;
                _interactiveSession = session;
            }
            catch (Exception connectionException)
            {
                Exception failure = connectionException;

                if (_backendRuntime.InteractiveSessionSnapshot.RequiresRecovery)
                {
                    try
                    {
                        await _lifetimeCoordinator.RecoverRuntimeAsync(CancellationToken.None);
                    }
                    catch (Exception recoveryException)
                    {
                        failure = new AggregateException(failure, recoveryException);
                    }
                }

                try
                {
                    await lease.DisposeAsync();
                }
                catch (Exception cleanupException)
                {
                    failure = new AggregateException(failure, cleanupException);
                }

                throw failure;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<IEndpoints> GetEndpointsAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return _interactiveSession?.Endpoints
                ?? throw new InvalidOperationException("The interactive backend client is not connected.");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync(CancellationToken.None);
        Exception? failure = null;

        try
        {
            if (_disposed)
                return;

            _disposed = true;
            _backendRuntime.StateChanged -= HandleRuntimeStateChanged;

            try
            {
                await CloseConnectionAsync();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            StateChanged = null;
        }
        finally
        {
            _lifecycleLock.Release();
        }

        GC.SuppressFinalize(this);

        if (failure is not null)
            throw failure;
    }

    private async Task CloseConnectionAsync()
    {
        Exception? failure = null;
        var session = _interactiveSession;
        var lease = _runtimeLease;
        _interactiveSession = null;
        _runtimeLease = null;

        if (session is not null)
        {
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception exception)
            {
                failure = exception;

                try
                {
                    await _lifetimeCoordinator.RecoverRuntimeAsync(CancellationToken.None);
                }
                catch (Exception recoveryException)
                {
                    failure = new AggregateException(failure, recoveryException);
                }
            }
        }

        if (lease is not null)
        {
            try
            {
                await lease.DisposeAsync();
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException(failure, exception);
            }
        }

        if (failure is not null)
            throw failure;
    }

    private void HandleRuntimeStateChanged(
        object? sender,
        BackendRuntimeStateChangedEventArgs args)
    {
        if (_disposed)
            return;

        var handlers = StateChanged;
        if (handlers is null)
            return;

        foreach (EventHandler<BackendRuntimeStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(InProcessFrontendBackendClient));
    }
}
