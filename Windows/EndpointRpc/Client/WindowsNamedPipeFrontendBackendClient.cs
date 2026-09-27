using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Errors;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

public sealed class WindowsNamedPipeFrontendBackendClient :
    IFrontendBackendClient<IEndpoints>,
    IIntentionalAgentShutdownCoordinator
{
    private readonly IEndpointRpcClientConnector _connector;
    private readonly IEndpointRpcAgentConnection? _agentConnection;
    private readonly WindowsAgentHealthValidator _agentHealthValidator = new();
    private readonly int _maximumRecoveryAttempts;
    private readonly TimeSpan _recoveryDelay;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeSource = new();
    private readonly object _snapshotGate = new();
    private readonly object _taskGate = new();
    private readonly object _suppressionGate = new();
    private readonly object _observerGate = new();
    private readonly ActiveObserverTaskTracker _observerTaskTracker = new();
    private IEndpointRpcTransport? _transport;
    private NamedPipeEndpointsProxy? _proxy;
    private Task _recoveryTask = Task.CompletedTask;
    private CancellationTokenSource? _recoverySource;
    private CancellationTokenSource? _endpointObserverSource;
    private CancellationTokenSource? _agentObserverSource;
    private Task<bool>? _intentionalShutdownTask;
    private long _observedAgentGeneration;
    private long _endpointGeneration;
    private WindowsFrontendRecoverySuppressionState _suppressionState;
    private WindowsEndpointClientConnectionState _connectionState = WindowsEndpointClientConnectionState.Disconnected;
    private BackendRuntimeSnapshot _snapshot = new(
        BackendRuntimeState.NotStarted,
        BackendRuntimeFailureKind.None,
        null,
        DateTimeOffset.UtcNow);
    private bool _disposed;
    private int _disposeStarted;

    public WindowsNamedPipeFrontendBackendClient(
        string pipeName,
        WindowsUiIpcIdentity identity)
        : this(new WindowsNamedPipeEndpointRpcConnector(pipeName, identity))
    {
    }

    public WindowsNamedPipeFrontendBackendClient(IEndpointRpcClientConnector connector)
        : this(null, connector)
    {
    }

    public WindowsNamedPipeFrontendBackendClient(
        IEndpointRpcAgentConnection? agentConnection,
        IEndpointRpcClientConnector connector,
        int maximumRecoveryAttempts = 3,
        TimeSpan? recoveryDelay = null)
    {
        _agentConnection = agentConnection;
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        if (maximumRecoveryAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRecoveryAttempts));
        _maximumRecoveryAttempts = maximumRecoveryAttempts;
        _recoveryDelay = recoveryDelay ?? TimeSpan.FromMilliseconds(350);
        if (_recoveryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(recoveryDelay));
    }

    public BackendRuntimeSnapshot Snapshot
    {
        get
        {
            ThrowIfDisposed();
            lock (_snapshotGate)
                return _snapshot;
        }
    }

    public WindowsEndpointClientConnectionState ConnectionState
    {
        get
        {
            lock (_snapshotGate)
                return _connectionState;
        }
    }

    internal int ActiveObserverCount => _observerTaskTracker.ActiveCount;

    internal WindowsFrontendRecoverySuppressionState RecoverySuppressionState
    {
        get
        {
            lock (_suppressionGate)
                return _suppressionState;
        }
    }

    public event EventHandler<BackendRuntimeStateChangedEventArgs>? StateChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNewWorkRejected();
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfNewWorkRejected();
            if (_transport?.IsConnected == true && _proxy is not null)
            {
                await EnsureAgentHealthyAsync(cancellationToken);
                ChangeState(BackendRuntimeState.Ready, BackendRuntimeFailureKind.None, null);
                ChangeConnectionState(WindowsEndpointClientConnectionState.Ready);
                return;
            }

            ChangeConnectionState(WindowsEndpointClientConnectionState.Connecting);
            if (_agentConnection is not null &&
                !await _agentConnection.EnsureConnectedAsync(cancellationToken))
            {
                throw new EndpointRpcDisconnectedException(
                    new InvalidOperationException("The Windows agent control connection is unavailable."));
            }
            await EnsureAgentHealthyAsync(cancellationToken);

            ChangeState(BackendRuntimeState.Starting, BackendRuntimeFailureKind.None, null);
            await DisposeEndpointConnectionLockedAsync();
            await ConnectEndpointLockedAsync(cancellationToken);
            await EnsureAgentHealthyAsync(cancellationToken);
            StartAgentObserverLocked();
            ChangeState(BackendRuntimeState.Ready, BackendRuntimeFailureKind.None, null);
            ChangeConnectionState(WindowsEndpointClientConnectionState.Ready);
        }
        catch (Exception exception)
        {
            var mapped = await MapAgentRuntimeFailureAsync(exception, cancellationToken);
            ChangeState(BackendRuntimeState.Failed, mapped.FailureKind, mapped.Exception);
            ChangeConnectionState(WindowsEndpointClientConnectionState.Unavailable);
            if (!ReferenceEquals(mapped.Exception, exception))
                throw mapped.Exception;
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNewWorkRejected();
        cancellationToken.ThrowIfCancellationRequested();
        if (Snapshot.State != BackendRuntimeState.Ready || _transport?.IsConnected != true)
            throw new InvalidOperationException("The endpoint backend client is not ready.");
        return Task.CompletedTask;
    }

    public async Task ResetDatabaseAndRestartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNewWorkRejected();
        if (_agentConnection is null)
        {
            throw new NotSupportedException(
                "Database reset requires the agent control connection.");
        }
        if (!TryEnterDatabaseResetSuppression())
            throw CreateShuttingDownException();

        await CancelRecoveryAsync();
        var recoveryAllowed = true;
        try
        {
            await _lifecycleLock.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed();
                ChangeState(BackendRuntimeState.Stopping, BackendRuntimeFailureKind.None, null);
                ChangeConnectionState(WindowsEndpointClientConnectionState.Unavailable);
                await DisposeEndpointConnectionLockedAsync();
            }
            finally
            {
                _lifecycleLock.Release();
            }

            var result = await _agentConnection.ResetDatabaseAsync(cancellationToken);
            if (!result.Completed)
            {
                var failure = new InvalidOperationException(
                    result.SafeMessage ?? "The database reset did not complete.");
                ChangeState(
                    BackendRuntimeState.Failed,
                    result.RequiresProcessRestart
                        ? BackendRuntimeFailureKind.ShutdownFailure
                        : BackendRuntimeFailureKind.StorageUnavailable,
                    failure);
                if (result.RequiresProcessRestart)
                {
                    recoveryAllowed = false;
                    CancelAgentObserverLocked();
                    recoveryAllowed = await _agentConnection.PrepareForReplacementAsync(
                        cancellationToken);
                }
                throw failure;
            }

            ExitDatabaseResetSuppression();
            await ConnectAsync(cancellationToken);
        }
        finally
        {
            ExitDatabaseResetSuppression();
            if (recoveryAllowed && CanRecover() && _transport?.IsConnected != true)
                StartRecovery();
        }
    }

    public async Task<IEndpoints> GetEndpointsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfNewWorkRejected();
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfNewWorkRejected();
            cancellationToken.ThrowIfCancellationRequested();
            if (_transport?.IsConnected != true || _proxy is null ||
                Snapshot.State != BackendRuntimeState.Ready)
            {
                throw new InvalidOperationException("The endpoint backend client is unavailable.");
            }
            return _proxy;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public Task<bool> BeginIntentionalAgentShutdownAsync(
        CancellationToken cancellationToken = default)
    {
        Task<bool> shutdownTask;
        lock (_taskGate)
            shutdownTask = _intentionalShutdownTask ??= BeginIntentionalAgentShutdownCoreAsync(cancellationToken);
        return ObserveIntentionalShutdownResultAsync(shutdownTask);
    }

    public Task CancelIntentionalAgentShutdownAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var changed = false;
        lock (_suppressionGate)
        {
            if (_suppressionState == WindowsFrontendRecoverySuppressionState.IntentionalShutdown)
            {
                _suppressionState = WindowsFrontendRecoverySuppressionState.None;
                changed = true;
            }
        }
        if (!changed)
            return Task.CompletedTask;

        lock (_taskGate)
            _intentionalShutdownTask = null;

        ChangeState(
            BackendRuntimeState.Failed,
            BackendRuntimeFailureKind.StorageUnavailable,
            new InvalidOperationException("The intentional agent exit was rejected before shutdown."));
        ChangeConnectionState(WindowsEndpointClientConnectionState.Unavailable);
        StartRecovery();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        lock (_suppressionGate)
            _suppressionState = WindowsFrontendRecoverySuppressionState.Disposed;
        ChangeConnectionState(WindowsEndpointClientConnectionState.Disposed);
        _lifetimeSource.Cancel();
        CancelEndpointObserverLocked();
        CancelAgentObserverLocked();
        await CancelRecoveryAsync();

        Exception? failure = null;
        await _lifecycleLock.WaitAsync(CancellationToken.None);
        try
        {
            _disposed = true;
            ChangeState(BackendRuntimeState.Stopping, BackendRuntimeFailureKind.None, null);
            try
            {
                await DisposeEndpointConnectionLockedAsync();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }

        if (_agentConnection is not null)
        {
            try
            {
                await _agentConnection.DisposeAsync();
            }
            catch (Exception exception)
            {
                failure = Combine(failure, exception);
            }
        }

        try
        {
            await _observerTaskTracker.WaitForCompletionAsync();
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }

        ChangeState(
            BackendRuntimeState.Stopped,
            failure is null ? BackendRuntimeFailureKind.None : BackendRuntimeFailureKind.ShutdownFailure,
            failure);
        StateChanged = null;
        lock (_taskGate)
        {
            _recoverySource?.Dispose();
            _recoverySource = null;
        }
        _lifetimeSource.Dispose();
        _lifecycleLock.Dispose();
        GC.SuppressFinalize(this);

        if (failure is not null)
            throw failure;
    }

    private async Task<bool> ObserveIntentionalShutdownResultAsync(Task<bool> shutdownTask)
    {
        try
        {
            var result = await shutdownTask;
            if (!result)
                ClearIntentionalShutdownTask(shutdownTask);
            return result;
        }
        catch
        {
            ClearIntentionalShutdownTask(shutdownTask);
            throw;
        }
    }

    private void ClearIntentionalShutdownTask(Task<bool> shutdownTask)
    {
        lock (_taskGate)
        {
            if (ReferenceEquals(_intentionalShutdownTask, shutdownTask))
                _intentionalShutdownTask = null;
        }
    }

    private async Task<bool> BeginIntentionalAgentShutdownCoreAsync(
        CancellationToken cancellationToken)
    {
        lock (_suppressionGate)
        {
            if (_suppressionState == WindowsFrontendRecoverySuppressionState.IntentionalShutdown)
                return true;
            if (_suppressionState != WindowsFrontendRecoverySuppressionState.None)
                return false;
            // Suppression is visible before recovery cancellation or connection disposal begins.
            _suppressionState = WindowsFrontendRecoverySuppressionState.IntentionalShutdown;
        }

        try
        {
            await CancelRecoveryAsync();
            await _lifecycleLock.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed();
                ChangeState(BackendRuntimeState.Stopping, BackendRuntimeFailureKind.None, null);
                ChangeConnectionState(WindowsEndpointClientConnectionState.IntentionalShutdown);
                await DisposeEndpointConnectionLockedAsync();
            }
            finally
            {
                _lifecycleLock.Release();
            }

            if (_agentConnection is not null)
            {
                CancelAgentObserverLocked();
                await _agentConnection.DisconnectAsync(cancellationToken);
            }
            return true;
        }
        catch
        {
            await CancelIntentionalAgentShutdownAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task ConnectEndpointLockedAsync(CancellationToken cancellationToken)
    {
        var transport = await _connector.ConnectAsync(cancellationToken);
        if (!transport.IsConnected)
        {
            await transport.DisposeAsync();
            throw new EndpointRpcDisconnectedException();
        }

        var generation = checked(++_endpointGeneration);
        _transport = transport;
        _proxy = new NamedPipeEndpointsProxy(
            transport,
            new EndpointRpcSerializer(),
            new EndpointRpcContractValidator());
        var observerToken = ReplaceEndpointObserver();
        var completion = transport.Completion;
        _observerTaskTracker.Start(
            () => ObserveEndpointConnectionAsync(
                generation,
                transport,
                completion,
                observerToken));
    }

    private void StartAgentObserverLocked()
    {
        if (_agentConnection is null || !_agentConnection.IsConnected)
            return;

        var generation = _agentConnection.ConnectionGeneration;
        var observerToken = ReplaceAgentObserver(generation);
        if (!observerToken.HasValue)
            return;
        var completion = _agentConnection.Completion;
        _observerTaskTracker.Start(
            () => ObserveAgentConnectionAsync(generation, completion, observerToken.Value));
    }

    private async Task ObserveEndpointConnectionAsync(
        long generation,
        IEndpointRpcTransport transport,
        Task completion,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await completion.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveLateCompletionFailure(completion);
            return;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        await HandleObservedConnectionLossAsync(
            endpointGeneration: generation,
            agentGeneration: null,
            expectedTransport: transport,
            failure: failure ?? new EndpointRpcDisconnectedException());
    }

    private async Task ObserveAgentConnectionAsync(
        long generation,
        Task completion,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await completion.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveLateCompletionFailure(completion);
            return;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (_agentConnection is null ||
            _agentConnection.ConnectionGeneration != generation)
        {
            return;
        }

        await HandleObservedConnectionLossAsync(
            endpointGeneration: null,
            agentGeneration: generation,
            expectedTransport: null,
            failure: failure ?? new EndpointRpcDisconnectedException(
                new InvalidOperationException("The Windows agent control connection was lost.")));
    }

    private async Task HandleObservedConnectionLossAsync(
        long? endpointGeneration,
        long? agentGeneration,
        IEndpointRpcTransport? expectedTransport,
        Exception failure)
    {
        try
        {
            await HandleConnectionLossAsync(
                endpointGeneration,
                agentGeneration,
                expectedTransport,
                failure);
        }
        catch (Exception cleanupFailure)
        {
            if (!CanRecover() ||
                !IsObservedConnectionCurrent(
                    endpointGeneration,
                    agentGeneration,
                    expectedTransport))
            {
                return;
            }

            ChangeState(
                BackendRuntimeState.Failed,
                BackendRuntimeFailureKind.StorageUnavailable,
                Combine(failure, cleanupFailure));
            ChangeConnectionState(WindowsEndpointClientConnectionState.Unavailable);
            StartRecovery();
        }
    }

    private async Task HandleConnectionLossAsync(
        long? endpointGeneration,
        long? agentGeneration,
        IEndpointRpcTransport? expectedTransport,
        Exception failure)
    {
        if (!CanRecover())
            return;

        await _lifecycleLock.WaitAsync(CancellationToken.None);
        try
        {
            if (!CanRecover())
                return;
            if (!IsObservedConnectionCurrent(
                    endpointGeneration,
                    agentGeneration,
                    expectedTransport))
            {
                return;
            }

            ChangeState(
                BackendRuntimeState.Failed,
                BackendRuntimeFailureKind.StorageUnavailable,
                failure);
            ChangeConnectionState(WindowsEndpointClientConnectionState.Unavailable);
            await DisposeEndpointConnectionLockedAsync();
        }
        finally
        {
            _lifecycleLock.Release();
        }

        StartRecovery();
    }

    private bool IsObservedConnectionCurrent(
        long? endpointGeneration,
        long? agentGeneration,
        IEndpointRpcTransport? expectedTransport)
    {
        if (endpointGeneration.HasValue && endpointGeneration.Value != _endpointGeneration)
            return false;
        if (expectedTransport is not null && !ReferenceEquals(_transport, expectedTransport))
            return false;
        if (agentGeneration.HasValue &&
            (_agentConnection is null ||
                _agentConnection.ConnectionGeneration != agentGeneration.Value))
        {
            return false;
        }
        return true;
    }

    private void StartRecovery()
    {
        if (!CanRecover() || _agentConnection is null)
            return;

        lock (_taskGate)
        {
            if (!CanRecover() || !_recoveryTask.IsCompleted)
                return;

            _recoverySource?.Dispose();
            _recoverySource = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeSource.Token);
            var source = _recoverySource;
            var recoveryTask = RecoverAsync(source.Token);
            _recoveryTask = recoveryTask;
            _ = recoveryTask.ContinueWith(
                completed =>
                {
                    _ = completed.Exception;
                    lock (_taskGate)
                    {
                        if (!ReferenceEquals(_recoveryTask, completed))
                            return;
                        if (ReferenceEquals(_recoverySource, source))
                        {
                            _recoverySource.Dispose();
                            _recoverySource = null;
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task CancelRecoveryAsync()
    {
        Task recoveryTask;
        lock (_taskGate)
        {
            _recoverySource?.Cancel();
            recoveryTask = _recoveryTask;
        }

        try
        {
            await recoveryTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        if (!CanRecover() || _agentConnection is null)
            return;

        ChangeConnectionState(WindowsEndpointClientConnectionState.Reconnecting);
        var replacementPreparationUsed = false;
        for (var attempt = 0; attempt < _maximumRecoveryAttempts; attempt++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CanRecover())
                    return;
                if (attempt > 0)
                    await Task.Delay(_recoveryDelay, cancellationToken);

                if (!await _agentConnection.EnsureConnectedAsync(cancellationToken))
                    continue;
                await EnsureAgentHealthyAsync(cancellationToken);

                await _lifecycleLock.WaitAsync(cancellationToken);
                try
                {
                    if (!CanRecover())
                        return;

                    ChangeState(BackendRuntimeState.Starting, BackendRuntimeFailureKind.None, null);
                    await DisposeEndpointConnectionLockedAsync();
                    await ConnectEndpointLockedAsync(cancellationToken);
                    await EnsureAgentHealthyAsync(cancellationToken);
                    StartAgentObserverLocked();
                    ChangeState(BackendRuntimeState.Ready, BackendRuntimeFailureKind.None, null);
                    ChangeConnectionState(WindowsEndpointClientConnectionState.Ready);
                    return;
                }
                finally
                {
                    _lifecycleLock.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                if (cancellationToken.IsCancellationRequested || !CanRecover())
                    return;

                (Exception Exception, BackendRuntimeFailureKind FailureKind) mapped;
                try
                {
                    mapped = await MapAgentRuntimeFailureAsync(exception, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                ChangeState(
                    BackendRuntimeState.Failed,
                    mapped.FailureKind == BackendRuntimeFailureKind.StartupFailure
                        ? BackendRuntimeFailureKind.StorageUnavailable
                        : mapped.FailureKind,
                    mapped.Exception);
                if (mapped.FailureKind == BackendRuntimeFailureKind.ShutdownFailure)
                {
                    try
                    {
                        CancelAgentObserverLocked();
                        if (!await _agentConnection.PrepareForReplacementAsync(cancellationToken))
                        {
                            ChangeConnectionState(WindowsEndpointClientConnectionState.Unavailable);
                            return;
                        }
                        if (!replacementPreparationUsed)
                        {
                            replacementPreparationUsed = true;
                            attempt--;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch
                    {
                        ChangeConnectionState(WindowsEndpointClientConnectionState.Unavailable);
                        return;
                    }
                }
            }
        }

        ChangeConnectionState(WindowsEndpointClientConnectionState.Unavailable);
    }

    private async Task DisposeEndpointConnectionLockedAsync()
    {
        CancelEndpointObserverLocked();
        var transport = _transport;
        var proxy = _proxy;
        _transport = null;
        _proxy = null;
        checked { _endpointGeneration++; }

        Exception? failure = null;
        if (transport is not null)
        {
            try { await transport.DisposeAsync(); }
            catch (Exception exception) { failure = exception; }
        }
        if (proxy is not null)
        {
            try { await proxy.DisposeAsync(); }
            catch (Exception exception) { failure = Combine(failure, exception); }
        }
        if (failure is not null)
            throw failure;
    }

    private async Task<(Exception Exception, BackendRuntimeFailureKind FailureKind)> MapAgentRuntimeFailureAsync(
        Exception original,
        CancellationToken cancellationToken)
    {
        if (_agentConnection is null || !_agentConnection.IsConnected)
            return (original, BackendRuntimeFailureKind.StartupFailure);

        try
        {
            var agentStatus = await _agentConnection.GetAgentStatusAsync(cancellationToken);
            var status = await _agentConnection.GetBackendRuntimeStatusAsync(cancellationToken);
            if (_agentHealthValidator.RequiresProcessReplacement(agentStatus, status))
            {
                return (
                    new InvalidOperationException(
                        "The Windows agent is not healthy enough to serve the frontend.",
                        original),
                    BackendRuntimeFailureKind.ShutdownFailure);
            }

            return status.FailureKind switch
            {
                BackendRuntimeFailureStatusKind.DatabaseCompatibility =>
                    (CreateDatabaseCompatibilityException(status, original),
                     BackendRuntimeFailureKind.DatabaseCompatibility),
                BackendRuntimeFailureStatusKind.PlatformKeyUnavailable =>
                    (new KeyProtectorUnavailableException(
                        PasswordManagerLocal.Common.Contracts.Security.KeyProtectorUnavailableReason.PlatformKeyStoreUnavailable,
                        original),
                     BackendRuntimeFailureKind.PlatformKeyUnavailable),
                BackendRuntimeFailureStatusKind.StorageUnavailable =>
                    (original, BackendRuntimeFailureKind.StorageUnavailable),
                BackendRuntimeFailureStatusKind.InteractiveCleanupFailure =>
                    (original, BackendRuntimeFailureKind.InteractiveCleanupFailure),
                BackendRuntimeFailureStatusKind.ShutdownFailure =>
                    (original, BackendRuntimeFailureKind.ShutdownFailure),
                _ => (original, BackendRuntimeFailureKind.StartupFailure)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (original, BackendRuntimeFailureKind.StartupFailure);
        }
    }

    private static DatabaseVersionNotSupportedException CreateDatabaseCompatibilityException(
        BackendRuntimeStatusDto status,
        Exception original)
    {
        var compatibility = status.DatabaseCompatibility
            ?? throw new InvalidOperationException(
                "The Windows agent omitted database compatibility details.",
                original);

        return new DatabaseVersionNotSupportedException(
            compatibility.DetectedVersion,
            compatibility.OldestSupportedVersion,
            compatibility.CurrentVersion,
            innerException: original);
    }

    private async Task EnsureAgentHealthyAsync(CancellationToken cancellationToken)
    {
        if (_agentConnection is null)
            return;

        var agentStatus = await _agentConnection.GetAgentStatusAsync(cancellationToken);
        var backendStatus = await _agentConnection.GetBackendRuntimeStatusAsync(cancellationToken);
        if (!_agentHealthValidator.IsHealthyForEndpoint(agentStatus, backendStatus))
        {
            throw new InvalidOperationException(
                "The Windows agent is not healthy enough to serve the frontend.");
        }
    }

    private CancellationToken ReplaceEndpointObserver()
    {
        CancellationTokenSource? previous;
        CancellationTokenSource current;
        lock (_observerGate)
        {
            previous = _endpointObserverSource;
            current = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeSource.Token);
            _endpointObserverSource = current;
        }
        CancelAndDispose(previous);
        return current.Token;
    }

    private CancellationToken? ReplaceAgentObserver(long generation)
    {
        CancellationTokenSource? previous;
        CancellationTokenSource current;
        lock (_observerGate)
        {
            if (_observedAgentGeneration == generation && _agentObserverSource is not null)
                return null;

            previous = _agentObserverSource;
            current = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeSource.Token);
            _agentObserverSource = current;
            _observedAgentGeneration = generation;
        }
        CancelAndDispose(previous);
        return current.Token;
    }

    private void CancelEndpointObserverLocked()
    {
        CancellationTokenSource? source;
        lock (_observerGate)
        {
            source = _endpointObserverSource;
            _endpointObserverSource = null;
        }
        CancelAndDispose(source);
    }

    private void CancelAgentObserverLocked()
    {
        CancellationTokenSource? source;
        lock (_observerGate)
        {
            source = _agentObserverSource;
            _agentObserverSource = null;
            _observedAgentGeneration = 0;
        }
        CancelAndDispose(source);
    }

    private static void CancelAndDispose(CancellationTokenSource? source)
    {
        if (source is null)
            return;
        source.Cancel();
        source.Dispose();
    }

    private static void ObserveLateCompletionFailure(Task completion)
    {
        if (completion.IsCompleted)
        {
            _ = completion.Exception;
            return;
        }

        _ = completion.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private bool TryEnterDatabaseResetSuppression()
    {
        lock (_suppressionGate)
        {
            if (_suppressionState != WindowsFrontendRecoverySuppressionState.None)
                return false;
            _suppressionState = WindowsFrontendRecoverySuppressionState.DatabaseReset;
            return true;
        }
    }

    private void ExitDatabaseResetSuppression()
    {
        lock (_suppressionGate)
        {
            if (_suppressionState == WindowsFrontendRecoverySuppressionState.DatabaseReset)
                _suppressionState = WindowsFrontendRecoverySuppressionState.None;
        }
    }

    private bool CanRecover()
    {
        if (_disposed || Volatile.Read(ref _disposeStarted) != 0)
            return false;
        lock (_suppressionGate)
            return _suppressionState == WindowsFrontendRecoverySuppressionState.None;
    }

    private void ChangeState(
        BackendRuntimeState state,
        BackendRuntimeFailureKind failureKind,
        Exception? failure)
    {
        BackendRuntimeSnapshot previous;
        BackendRuntimeSnapshot current;
        lock (_snapshotGate)
        {
            previous = _snapshot;
            current = new BackendRuntimeSnapshot(
                state,
                failureKind,
                failure,
                DateTimeOffset.UtcNow);
            _snapshot = current;
        }

        if (previous == current)
            return;

        var handlers = StateChanged;
        if (handlers is null)
            return;

        var args = new BackendRuntimeStateChangedEventArgs(previous, current);
        foreach (EventHandler<BackendRuntimeStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); } catch { }
        }
    }

    private void ChangeConnectionState(WindowsEndpointClientConnectionState state)
    {
        lock (_snapshotGate)
            _connectionState = state;
    }

    private void ThrowIfNewWorkRejected()
    {
        ThrowIfDisposed();
        lock (_suppressionGate)
        {
            if (_suppressionState == WindowsFrontendRecoverySuppressionState.IntentionalShutdown)
                throw CreateShuttingDownException();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed || Volatile.Read(ref _disposeStarted) != 0)
            throw new ObjectDisposedException(nameof(WindowsNamedPipeFrontendBackendClient));
    }

    private InvalidOperationException CreateShuttingDownException() =>
        new("The Windows frontend backend client is shutting down intentionally.");

    private Exception Combine(Exception? first, Exception second) =>
        first is null ? second : new AggregateException(first, second);
}
