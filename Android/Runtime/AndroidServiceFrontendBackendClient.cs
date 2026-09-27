using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;

namespace PasswordManagerLocal.Android.Runtime;

public sealed class AndroidServiceFrontendBackendClient : IFrontendBackendClient<IEndpoints>
{
    private readonly AndroidRuntimeServiceHost _owner;
    private readonly IBackendRuntime _runtime;
    private readonly IBackendRuntimeLifetimeCoordinator _lifetimeCoordinator;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly TaskCompletionSource _disposeCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private IBackendRuntimeLease? _interactiveLease;
    private IInteractiveBackendSession? _interactiveSession;
    private Exception? _databaseRecoveryFailure;
    private int _attachmentState = (int)AndroidInteractiveAttachmentState.Initializing;

    internal AndroidServiceFrontendBackendClient(
        AndroidRuntimeServiceHost owner,
        IBackendRuntime runtime,
        IBackendRuntimeLifetimeCoordinator lifetimeCoordinator,
        long attachmentGeneration)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _lifetimeCoordinator = lifetimeCoordinator
            ?? throw new ArgumentNullException(nameof(lifetimeCoordinator));
        AttachmentGeneration = attachmentGeneration;
        _runtime.StateChanged += HandleRuntimeStateChanged;
    }

    internal long AttachmentGeneration { get; }

    internal AndroidInteractiveAttachmentState AttachmentState =>
        (AndroidInteractiveAttachmentState)Volatile.Read(ref _attachmentState);

    internal bool HasMutationAuthority =>
        AttachmentState == AndroidInteractiveAttachmentState.Active;

    internal bool HasConnectionAuthority =>
        AttachmentState is AndroidInteractiveAttachmentState.Active or
            AndroidInteractiveAttachmentState.DatabaseRecovery;

    public BackendRuntimeSnapshot Snapshot
    {
        get
        {
            ThrowIfDisposed();
            return _runtime.Snapshot;
        }
    }

    public event EventHandler<BackendRuntimeStateChangedEventArgs>? StateChanged;

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        _owner.EnsureInteractiveConnectionAsync(this, cancellationToken);

    public async Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnavailable();
            if (_interactiveSession is null)
                throw new InvalidOperationException("The Android interactive service attachment is not connected.");

            await _runtime.WaitUntilReadyAsync(cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public Task ResetDatabaseAndRestartAsync(CancellationToken cancellationToken = default) =>
        _owner.ResetDatabaseAsync(this, cancellationToken);

    public async Task<IEndpoints> GetEndpointsAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnavailable();
            _owner.EnsureEndpointAuthority(this);
            cancellationToken.ThrowIfCancellationRequested();
            var endpoints = _interactiveSession?.Endpoints
                ?? throw new InvalidOperationException("The Android interactive service attachment is not connected.");
            return new AndroidAttachmentAuthorizedEndpoints(_owner, this, endpoints);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (TryBeginClosing())
        {
            await _owner.DetachInteractiveClientAsync(this);
            return;
        }

        if (AttachmentState == AndroidInteractiveAttachmentState.Disposed)
            return;

        await _disposeCompletion.Task;
    }

    internal void ActivateFromHost()
    {
        var previous = Interlocked.CompareExchange(
            ref _attachmentState,
            (int)AndroidInteractiveAttachmentState.Active,
            (int)AndroidInteractiveAttachmentState.Initializing);
        if (previous == (int)AndroidInteractiveAttachmentState.Active)
            return;
        if (previous != (int)AndroidInteractiveAttachmentState.Initializing)
            throw new InvalidOperationException("The Android interactive attachment can no longer become active.");
    }

    internal void ActivateDatabaseRecoveryFromHost(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        _databaseRecoveryFailure = failure;
        var previous = Interlocked.CompareExchange(
            ref _attachmentState,
            (int)AndroidInteractiveAttachmentState.DatabaseRecovery,
            (int)AndroidInteractiveAttachmentState.Initializing);
        if (previous == (int)AndroidInteractiveAttachmentState.DatabaseRecovery)
            return;
        if (previous != (int)AndroidInteractiveAttachmentState.Initializing)
            throw new InvalidOperationException("The Android interactive attachment can no longer enter database recovery.");
    }

    internal void BeginClosingFromHost() => TryBeginClosing();

    internal AndroidInteractiveAttachmentState SuspendAuthorityForResetFromHost()
    {
        while (true)
        {
            var state = AttachmentState;
            if (state is not AndroidInteractiveAttachmentState.Active and
                not AndroidInteractiveAttachmentState.DatabaseRecovery)
            {
                throw new InvalidOperationException("The Android interactive attachment cannot enter database reset.");
            }

            if (Interlocked.CompareExchange(
                    ref _attachmentState,
                    (int)AndroidInteractiveAttachmentState.Resetting,
                    (int)state) == (int)state)
            {
                return state;
            }
        }
    }

    internal void ResumeAuthorityAfterResetFromHost(
        AndroidInteractiveAttachmentState stateBeforeReset,
        bool replacementConnectionAdopted)
    {
        var targetState = replacementConnectionAdopted
            ? AndroidInteractiveAttachmentState.Active
            : stateBeforeReset;
        if (targetState is not AndroidInteractiveAttachmentState.Active and
            not AndroidInteractiveAttachmentState.DatabaseRecovery)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stateBeforeReset),
                stateBeforeReset,
                "Database reset can only restore an active or recovery attachment.");
        }

        var previous = Interlocked.CompareExchange(
            ref _attachmentState,
            (int)targetState,
            (int)AndroidInteractiveAttachmentState.Resetting);
        if (previous is (int)AndroidInteractiveAttachmentState.Active or
            (int)AndroidInteractiveAttachmentState.DatabaseRecovery or
            (int)AndroidInteractiveAttachmentState.Closing or
            (int)AndroidInteractiveAttachmentState.Disposed)
        {
            return;
        }

        if (previous != (int)AndroidInteractiveAttachmentState.Resetting)
            throw new InvalidOperationException("The Android interactive attachment cannot leave database reset.");
    }

    internal async Task<AndroidInteractiveOpenResult> OpenFromHostAsync(
        CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnavailable();
            if (_interactiveSession is not null)
                return new AndroidInteractiveOpenResult(true, true, null);
            if (AttachmentState == AndroidInteractiveAttachmentState.DatabaseRecovery &&
                _databaseRecoveryFailure is not null)
            {
                return new AndroidInteractiveOpenResult(false, true, _databaseRecoveryFailure);
            }

            IBackendRuntimeLease lease;
            try
            {
                lease = await _lifetimeCoordinator.AcquireAsync(
                    BackendLifetimeReason.InteractiveUi,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                return new AndroidInteractiveOpenResult(false, true, exception);
            }

            try
            {
                var session = await _runtime.OpenInteractiveSessionAsync(cancellationToken);
                _interactiveLease = lease;
                _interactiveSession = session;
                return new AndroidInteractiveOpenResult(true, true, null);
            }
            catch (Exception connectionException)
            {
                Exception failure = connectionException;
                var runtimeSafe = true;
                if (_runtime.InteractiveSessionSnapshot.RequiresRecovery)
                {
                    try
                    {
                        await _lifetimeCoordinator.RecoverRuntimeAsync(CancellationToken.None);
                        if (_runtime.InteractiveSessionSnapshot.RequiresRecovery)
                        {
                            throw new InvalidOperationException(
                                "The Android runtime remained unsafe after interactive recovery.");
                        }
                    }
                    catch (Exception recoveryException)
                    {
                        runtimeSafe = false;
                        failure = new AggregateException(failure, recoveryException);
                    }
                }

                try
                {
                    await lease.DisposeAsync();
                }
                catch (Exception cleanupException)
                {
                    runtimeSafe = false;
                    failure = new AggregateException(failure, cleanupException);
                }

                return new AndroidInteractiveOpenResult(false, runtimeSafe, failure);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    internal async Task<AndroidInteractiveCleanupResult> CloseFromHostAsync()
    {
        await _lifecycleLock.WaitAsync(CancellationToken.None);
        try
        {
            var session = _interactiveSession;
            var lease = _interactiveLease;
            _interactiveSession = null;
            _interactiveLease = null;

            Exception? cleanupFailure = null;
            Exception? recoveryFailure = null;
            Exception? leaseFailure = null;

            if (session is not null)
            {
                try
                {
                    await session.DisposeAsync();
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                    try
                    {
                        await _lifetimeCoordinator.RecoverRuntimeAsync(CancellationToken.None);
                        if (_runtime.InteractiveSessionSnapshot.RequiresRecovery)
                        {
                            throw new InvalidOperationException(
                                "The Android runtime remained unsafe after interactive recovery.");
                        }
                    }
                    catch (Exception exceptionDuringRecovery)
                    {
                        recoveryFailure = exceptionDuringRecovery;
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
                    leaseFailure = exception;
                }
            }

            var failure = CombineFailures(cleanupFailure, recoveryFailure, leaseFailure);
            if (recoveryFailure is not null || leaseFailure is not null)
            {
                return new AndroidInteractiveCleanupResult(
                    AndroidInteractiveCleanupOutcome.RuntimeUnsafe,
                    failure);
            }

            if (cleanupFailure is not null)
            {
                return new AndroidInteractiveCleanupResult(
                    AndroidInteractiveCleanupOutcome.Recovered,
                    cleanupFailure);
            }

            return new AndroidInteractiveCleanupResult(
                AndroidInteractiveCleanupOutcome.Succeeded,
                null);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    internal async Task AdoptResetConnectionFromHostAsync(
        IBackendRuntimeLease lease,
        IInteractiveBackendSession session)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(session);

        await _lifecycleLock.WaitAsync(CancellationToken.None);
        try
        {
            if (AttachmentState != AndroidInteractiveAttachmentState.Resetting)
                throw new InvalidOperationException("The Android interactive attachment is not completing a database reset.");
            if (_interactiveLease is not null || _interactiveSession is not null)
                throw new InvalidOperationException("The Android interactive connection is already active.");

            _interactiveLease = lease;
            _interactiveSession = session;
            _databaseRecoveryFailure = null;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    internal void CompleteDisposalFromHost()
    {
        if (Interlocked.Exchange(
                ref _attachmentState,
                (int)AndroidInteractiveAttachmentState.Disposed) ==
            (int)AndroidInteractiveAttachmentState.Disposed)
        {
            return;
        }

        _runtime.StateChanged -= HandleRuntimeStateChanged;
        StateChanged = null;
        _disposeCompletion.TrySetResult();
        GC.SuppressFinalize(this);
    }

    private bool TryBeginClosing()
    {
        while (true)
        {
            var state = AttachmentState;
            if (state is AndroidInteractiveAttachmentState.Closing or
                AndroidInteractiveAttachmentState.Disposed)
            {
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _attachmentState,
                    (int)AndroidInteractiveAttachmentState.Closing,
                    (int)state) == (int)state)
            {
                return true;
            }
        }
    }

    private void HandleRuntimeStateChanged(
        object? sender,
        BackendRuntimeStateChangedEventArgs args)
    {
        if (AttachmentState == AndroidInteractiveAttachmentState.Disposed)
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

    private void ThrowIfUnavailable()
    {
        var state = AttachmentState;
        if (state == AndroidInteractiveAttachmentState.Resetting)
            throw new InvalidOperationException("The Android interactive attachment is temporarily unavailable during database reset.");
        if (state is AndroidInteractiveAttachmentState.Closing or
            AndroidInteractiveAttachmentState.Disposed)
        {
            throw new ObjectDisposedException(nameof(AndroidServiceFrontendBackendClient));
        }
    }

    private void ThrowIfDisposed()
    {
        if (AttachmentState == AndroidInteractiveAttachmentState.Disposed)
            throw new ObjectDisposedException(nameof(AndroidServiceFrontendBackendClient));
    }

    private Exception? CombineFailures(params Exception?[] failures)
    {
        var present = failures.Where(static failure => failure is not null).Cast<Exception>().ToArray();
        return present.Length switch
        {
            0 => null,
            1 => present[0],
            _ => new AggregateException(present)
        };
    }
}
