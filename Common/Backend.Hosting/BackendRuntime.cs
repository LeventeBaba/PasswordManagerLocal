using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Diagnostics;
using SQLitePCL;

namespace PasswordManagerLocal.Common.Backend.Hosting;

internal sealed class BackendRuntime : IBackendRuntime, IBackendExecutionProfileProviderSink
{
    private readonly BackendRuntimeOptions _options;
    private readonly BackendStorageCleaner _storageCleaner;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _interactiveSessionLock = new(1, 1);
    private IBackendExecutionProfileProvider? _executionProfileProvider;
    private BackendRuntimeSnapshot _snapshot = new(
        BackendRuntimeState.NotStarted,
        BackendRuntimeFailureKind.None,
        null,
        DateTimeOffset.UtcNow);
    private InteractiveSessionLifecycleSnapshot _interactiveSessionSnapshot = new(
        InteractiveSessionLifecycleState.None,
        null,
        DateTimeOffset.UtcNow);
    private SyncRuntimeSnapshot _syncSnapshot = new(SyncRuntimeState.Disabled, null);
    private BackendServiceHost? _host;
    private ISyncRuntimeService? _syncRuntime;
    private InteractiveBackendSession? _interactiveSession;
    private Task? _startupTask;
    private Task? _stopTask;
    private Exception? _restartBlockedFailure;
    private long _hostGeneration;
    private bool _disposeRequested;
    private bool _disposed;

    public BackendRuntime(BackendRuntimeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _storageCleaner = new BackendStorageCleaner(options.StoragePaths);
    }

    public BackendRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return _snapshot;
        }
    }

    public InteractiveSessionLifecycleSnapshot InteractiveSessionSnapshot
    {
        get
        {
            lock (_gate)
                return _interactiveSessionSnapshot;
        }
    }

    public SyncRuntimeSnapshot SyncSnapshot
    {
        get
        {
            lock (_gate)
                return _syncSnapshot;
        }
    }

    public event EventHandler<BackendRuntimeStateChangedEventArgs>? StateChanged;
    public event EventHandler<SyncRuntimeStateChangedEventArgs>? SyncStateChanged;

    void IBackendExecutionProfileProviderSink.SetExecutionProfileProvider(
        IBackendExecutionProfileProvider executionProfileProvider)
    {
        ArgumentNullException.ThrowIfNull(executionProfileProvider);

        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (_executionProfileProvider is not null)
                throw new InvalidOperationException("The backend execution-profile provider is already configured.");

            _executionProfileProvider = executionProfileProvider;
        }
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task operation;
            TaskCompletionSource? starter = null;
            BackendRuntimeStateChangedEventArgs? stateChange = null;
            var retryAfterOperation = false;

            lock (_gate)
            {
                ThrowIfDisposedLocked();
                ThrowIfRestartBlockedLocked();

                if (_snapshot.State == BackendRuntimeState.Ready)
                    return;

                if (_stopTask is not null)
                {
                    operation = _stopTask;
                    retryAfterOperation = true;
                }
                else if (_startupTask is not null)
                {
                    operation = _startupTask;
                }
                else
                {
                    starter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _startupTask = starter.Task;
                    operation = starter.Task;
                    stateChange = TransitionLocked(
                        BackendRuntimeState.Starting,
                        BackendRuntimeFailureKind.None,
                        null);
                }
            }

            Publish(stateChange);

            if (starter is not null)
                _ = RunStartupAsync(starter, resetStorageFirst: false);

            await operation.WaitAsync(cancellationToken);
            if (!retryAfterOperation)
                return;
        }
    }

    public async Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);

        var snapshot = Snapshot;
        if (!snapshot.IsReady)
            throw snapshot.Failure ?? new InvalidOperationException("The backend runtime is not ready.");
    }

    public async Task<IInteractiveBackendSession> OpenInteractiveSessionAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
            EnsureInteractiveSessionCanOpenLocked();

        await WaitUntilReadyAsync(cancellationToken);
        await _interactiveSessionLock.WaitAsync(cancellationToken);

        Exception? openingFailure = null;
        Exception? cleanupFailure = null;
        BackendRuntimeStateChangedEventArgs? stateChange = null;
        InteractiveBackendSession? openedSession = null;

        try
        {
            BackendServiceHost host;
            IInteractiveSessionStateService sessionState;
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                EnsureInteractiveSessionCanOpenLocked();

                host = _host is not null && _snapshot.State == BackendRuntimeState.Ready
                    ? _host
                    : throw new InvalidOperationException("The backend runtime is not ready.");
                sessionState = host.Services.GetRequiredService<IInteractiveSessionStateService>();
                SetInteractiveSessionSnapshotLocked(InteractiveSessionLifecycleState.Opening, null);
            }

            try
            {
                await host.StartInteractiveAsync(cancellationToken);
                await sessionState.ActivateAsync(cancellationToken);
                var profileLifecycle = _executionProfileProvider as IBackendExecutionProfileProviderLifecycle;
                if (profileLifecycle is not null)
                {
                    profileLifecycle.OpenEnrollmentAdmission();
                }
                else if (_options.ServiceHostFactory is null)
                {
                    throw new InvalidOperationException("The backend execution-profile provider is not configured.");
                }

                host.Services.GetService<IDeviceEnrollmentLifecycleCoordinator>()
                    ?.OpenInteractiveAdmission();
                var endpoints = host.Services.GetRequiredService<IEndpoints>();
                cancellationToken.ThrowIfCancellationRequested();
                openedSession = new InteractiveBackendSession(
                    endpoints,
                    sessionState,
                    CloseInteractiveSessionAsync);

                lock (_gate)
                {
                    ThrowIfDisposedLocked();
                    if (_snapshot.State != BackendRuntimeState.Ready ||
                        !ReferenceEquals(_host, host))
                    {
                        throw new InvalidOperationException(
                            "The backend runtime stopped while the interactive session was opening.");
                    }

                    _interactiveSession = openedSession;
                    SetInteractiveSessionSnapshotLocked(InteractiveSessionLifecycleState.Active, null);
                }

                BackendDebugLog.OperationCompleted("Interactive backend session opening", "Runtime");
            }
            catch (Exception exception)
            {
                openingFailure = exception;
                BackendDebugLog.Error("Interactive backend session opening failed.", exception, "Runtime");
                try
                {
                    await StopInteractiveStateAsync(host, null, CancellationToken.None);
                }
                catch (Exception exceptionDuringCleanup)
                {
                    cleanupFailure = exceptionDuringCleanup;
                }

                lock (_gate)
                {
                    _interactiveSession = null;
                    if (cleanupFailure is null)
                    {
                        SetInteractiveSessionSnapshotLocked(InteractiveSessionLifecycleState.None, null);
                    }
                    else
                    {
                        var openingCleanupFailure = new AggregateException(openingFailure, cleanupFailure);
                        SetInteractiveSessionSnapshotLocked(
                            InteractiveSessionLifecycleState.CleanupFailed,
                            openingCleanupFailure);
                        stateChange = TransitionLocked(
                            BackendRuntimeState.Failed,
                            BackendRuntimeFailureKind.InteractiveCleanupFailure,
                            openingCleanupFailure);
                    }
                }
            }
        }
        finally
        {
            _interactiveSessionLock.Release();
        }

        Publish(stateChange);

        if (openingFailure is null)
            return openedSession!;

        Exception failure = cleanupFailure is null
            ? openingFailure
            : new AggregateException(openingFailure, cleanupFailure);

        if (cleanupFailure is not null)
        {
            try
            {
                await StopAsync(CancellationToken.None);
            }
            catch (Exception stopFailure)
            {
                failure = new AggregateException(failure, stopFailure);
            }
        }

        throw failure;
    }

    public Task ResetDatabaseAndRestartAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource starter;
        BackendRuntimeStateChangedEventArgs? stateChange;

        lock (_gate)
        {
            ThrowIfDisposedLocked();

            if (_startupTask is not null || _stopTask is not null)
                throw new InvalidOperationException("A backend lifecycle operation is already in progress.");

            if (_snapshot.State != BackendRuntimeState.Failed ||
                _snapshot.FailureKind != BackendRuntimeFailureKind.DatabaseCompatibility)
            {
                throw new InvalidOperationException(
                    "The database can only be reset after a database compatibility startup failure.");
            }

            starter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _startupTask = starter.Task;
            stateChange = TransitionLocked(
                BackendRuntimeState.Starting,
                BackendRuntimeFailureKind.None,
                null);
        }

        Publish(stateChange);
        _ = RunStartupAsync(starter, resetStorageFirst: true);
        return starter.Task.WaitAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task? startupToWait;
            Task operation;
            TaskCompletionSource? stopper = null;
            BackendRuntimeStateChangedEventArgs? stateChange = null;

            lock (_gate)
            {
                if (_disposed)
                    return;

                if (_restartBlockedFailure is not null &&
                    _startupTask is null &&
                    _stopTask is null)
                {
                    throw new InvalidOperationException(
                        "The backend runtime cannot be restarted or stopped cleanly on this instance after an unsafe shutdown failure. Runtime recreation is required.",
                        _restartBlockedFailure);
                }

                startupToWait = _startupTask;
                if (startupToWait is not null)
                {
                    operation = startupToWait;
                }
                else if (_stopTask is not null)
                {
                    operation = _stopTask;
                }
                else if (_snapshot.State is BackendRuntimeState.NotStarted or BackendRuntimeState.Stopped)
                {
                    return;
                }
                else
                {
                    stopper = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _stopTask = stopper.Task;
                    operation = stopper.Task;
                    stateChange = TransitionLocked(
                        BackendRuntimeState.Stopping,
                        BackendRuntimeFailureKind.None,
                        null);
                }
            }

            Publish(stateChange);

            if (stopper is not null)
                _ = RunStopAsync(stopper);

            try
            {
                await operation.WaitAsync(cancellationToken);
            }
            catch when (startupToWait is not null && !cancellationToken.IsCancellationRequested)
            {
            }

            if (startupToWait is null)
                return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposeRequested = true;
        }

        if (_executionProfileProvider is IBackendExecutionProfileProviderLifecycle profileLifecycle)
            profileLifecycle.StopPublishing();

        try
        {
            await StopAsync(CancellationToken.None);
        }
        finally
        {
            lock (_gate)
                _disposed = true;

            GC.SuppressFinalize(this);
        }
    }

    private async ValueTask CloseInteractiveSessionAsync(InteractiveBackendSession session)
    {
        Exception? cleanupFailure = null;
        BackendRuntimeStateChangedEventArgs? stateChange = null;

        await _interactiveSessionLock.WaitAsync(CancellationToken.None);
        try
        {
            BackendServiceHost? host;
            lock (_gate)
            {
                if (!ReferenceEquals(_interactiveSession, session))
                    return;

                host = _host;
            }

            void MarkClosing() => MarkInteractiveSessionClosing(session);

            try
            {
                if (host is null)
                {
                    await session.BeginCloseAsync(MarkClosing);
                }
                else
                {
                    await StopInteractiveStateAsync(
                        host,
                        session,
                        CancellationToken.None,
                        MarkClosing);
                }
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }

            lock (_gate)
            {
                if (cleanupFailure is null)
                {
                    SetInteractiveSessionSnapshotLocked(InteractiveSessionLifecycleState.None, null);
                }
                else
                {
                    SetInteractiveSessionSnapshotLocked(
                        InteractiveSessionLifecycleState.CleanupFailed,
                        cleanupFailure);
                    stateChange = TransitionLocked(
                        BackendRuntimeState.Failed,
                        BackendRuntimeFailureKind.InteractiveCleanupFailure,
                        cleanupFailure);
                }
            }
        }
        finally
        {
            _interactiveSessionLock.Release();
        }

        Publish(stateChange);

        if (cleanupFailure is null)
            return;

        Exception failure = cleanupFailure;
        try
        {
            await StopAsync(CancellationToken.None);
        }
        catch (Exception stopFailure)
        {
            failure = new AggregateException(failure, stopFailure);
        }

        throw failure;
    }

    private async Task CloseInteractiveStateForShutdownAsync(BackendServiceHost? host)
    {
        await _interactiveSessionLock.WaitAsync(CancellationToken.None);
        try
        {
            InteractiveBackendSession? session;
            Exception? previousCleanupFailure = null;
            var preserveCleanupFailure = false;
            lock (_gate)
            {
                session = _interactiveSession;
                preserveCleanupFailure =
                    _interactiveSessionSnapshot.State == InteractiveSessionLifecycleState.CleanupFailed;
                previousCleanupFailure = preserveCleanupFailure
                    ? _interactiveSessionSnapshot.Failure
                    : null;
                if (session is null &&
                    !preserveCleanupFailure &&
                    _interactiveSessionSnapshot.State != InteractiveSessionLifecycleState.None)
                {
                    SetInteractiveSessionSnapshotLocked(InteractiveSessionLifecycleState.Closing, null);
                }
            }

            void MarkClosing()
            {
                if (session is not null)
                    MarkInteractiveSessionClosing(session);
            }

            Exception? failure = null;
            try
            {
                if (host is not null)
                {
                    await StopInteractiveStateAsync(
                        host,
                        session,
                        CancellationToken.None,
                        session is null ? null : MarkClosing);
                }
                else if (session is not null)
                {
                    await session.BeginCloseAsync(MarkClosing);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
                BackendDebugLog.Error("Interactive backend session shutdown failed.", exception, "Runtime");
            }

            lock (_gate)
            {
                if (failure is not null)
                {
                    var combinedFailure = previousCleanupFailure is null
                        ? failure
                        : new AggregateException(previousCleanupFailure, failure);
                    SetInteractiveSessionSnapshotLocked(
                        InteractiveSessionLifecycleState.CleanupFailed,
                        combinedFailure);
                }
                else if (!preserveCleanupFailure)
                {
                    SetInteractiveSessionSnapshotLocked(InteractiveSessionLifecycleState.None, null);
                }
            }

            if (failure is not null)
                throw failure;
        }
        finally
        {
            _interactiveSessionLock.Release();
        }
    }

    private async Task StopInteractiveStateAsync(
        BackendServiceHost host,
        InteractiveBackendSession? session,
        CancellationToken cancellationToken,
        Action? closingStarted = null)
    {
        var failures = new List<Exception>();
        Task? sessionDrain = null;
        Task? stateDrain = null;
        var profileLifecycle = _executionProfileProvider as IBackendExecutionProfileProviderLifecycle;
        var enrollmentAdmissionClosed = false;

        void CloseEnrollmentAdmission()
        {
            if (enrollmentAdmissionClosed)
                return;

            profileLifecycle?.CloseEnrollmentAdmission();
            enrollmentAdmissionClosed = true;
        }

        if (session is not null)
        {
            try
            {
                sessionDrain = session.BeginCloseAsync(() =>
                {
                    CloseEnrollmentAdmission();
                    closingStarted?.Invoke();
                });
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        else
        {
            CloseEnrollmentAdmission();
            try
            {
                stateDrain = host.Services
                    .GetRequiredService<IInteractiveSessionStateService>()
                    .DeactivateAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        CloseEnrollmentAdmission();

        Task? enrollmentDrain = null;
        try
        {
            var enrollmentLifecycle = host.Services.GetService<IDeviceEnrollmentLifecycleCoordinator>();
            if (enrollmentLifecycle is not null)
                enrollmentDrain = enrollmentLifecycle.CloseInteractiveAdmissionAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (sessionDrain is not null)
            await CaptureTaskFailuresAsync(sessionDrain, failures);

        if (stateDrain is not null)
            await CaptureTaskFailuresAsync(stateDrain, failures);

        if (enrollmentDrain is not null)
            await CaptureTaskFailuresAsync(enrollmentDrain, failures);

        try
        {
            await host.StopInteractiveAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await host.Services
                .GetRequiredService<IInteractiveSensitiveStateResetter>()
                .ResetAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count == 1)
            throw failures[0];
        if (failures.Count > 1)
            throw new AggregateException(failures);
    }


    private static async Task CaptureTaskFailuresAsync(
        Task task,
        ICollection<Exception> failures)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            if (task.Exception is { } aggregate)
                foreach (var inner in aggregate.Flatten().InnerExceptions)
                    failures.Add(inner);
            else
                failures.Add(exception);
        }
    }

    private async Task RunStartupAsync(TaskCompletionSource completion, bool resetStorageFirst)
    {
        BackendServiceHost? newHost = null;
        ISyncRuntimeService? newSyncRuntime = null;
        BackendRuntimeStateChangedEventArgs? finalStateChange = null;
        SyncRuntimeStateChangedEventArgs? syncStateChange = null;
        Exception? failure = null;
        long generation = 0;
        var lifecycleLockTaken = false;

        try
        {
            await _lifecycleLock.WaitAsync(CancellationToken.None);
            lifecycleLockTaken = true;

            await DisposeCurrentHostCoreAsync();

            BackendDebugLog.InitializeForCurrentBuild(_options.StoragePaths.LogsDirectory);
            BackendDebugLog.Info(
                resetStorageFirst
                    ? "Backend startup with storage reset started."
                    : "Backend startup started.",
                "Runtime");

            if (resetStorageFirst)
            {
                _storageCleaner.ClearSqlitePools();
                _storageCleaner.DeleteDatabaseFiles();
                BackendDebugLog.OperationCompleted("Backend storage reset", "Runtime");
            }

            (newHost, newSyncRuntime) = await CreateAndStartHostAsync();

            lock (_gate)
            {
                _host = newHost;
                _syncRuntime = newSyncRuntime;
                _restartBlockedFailure = null;
                newSyncRuntime.StateChanged += HandleSyncStateChanged;
                generation = ++_hostGeneration;
                syncStateChange = SetSyncSnapshotLocked(newSyncRuntime.Snapshot);
                SetInteractiveSessionSnapshotLocked(InteractiveSessionLifecycleState.None, null);
                finalStateChange = TransitionLocked(
                    BackendRuntimeState.Ready,
                    BackendRuntimeFailureKind.None,
                    null);
                _startupTask = null;
            }

            newHost = null;
            newSyncRuntime = null;
        }
        catch (Exception exception)
        {
            failure = exception;
            BackendDebugLog.Error("Backend startup failed.", exception, "Runtime");

            if (newSyncRuntime is not null)
                newSyncRuntime.StateChanged -= HandleSyncStateChanged;

            if (newHost is not null)
            {
                try
                {
                    await newHost.DisposeAsync();
                }
                catch (Exception disposeException)
                {
                    failure = new AggregateException(failure, disposeException);
                }
            }

            if (resetStorageFirst)
            {
                try
                {
                    _storageCleaner.ClearSqlitePools();
                    _storageCleaner.DeleteDatabaseFiles();
                }
                catch (Exception cleanupException)
                {
                    failure = new AggregateException(failure, cleanupException);
                }
            }

            var (state, failureKind) = ClassifyFailure(failure);
            lock (_gate)
            {
                _host = null;
                _syncRuntime = null;
                finalStateChange = TransitionLocked(state, failureKind, failure);
                syncStateChange = SetSyncSnapshotLocked(
                    new SyncRuntimeSnapshot(SyncRuntimeState.Disabled, null));
                _startupTask = null;
            }
        }
        finally
        {
            if (lifecycleLockTaken)
                _lifecycleLock.Release();
        }

        Publish(syncStateChange);
        Publish(finalStateChange);

        if (failure is null)
        {
            BackendDebugLog.Info(
                $"Backend startup completed successfully. HostGeneration={generation}.",
                "Runtime");
            completion.TrySetResult();
            _ = RunSyncStartupAsync(generation);
        }
        else
        {
            BackendDebugLog.Error("Backend startup completed in a failed state.", failure, "Runtime");
            completion.TrySetException(failure);
        }
    }

    private async Task<(BackendServiceHost Host, ISyncRuntimeService SyncRuntime)> CreateAndStartHostAsync()
    {
        BackendDebugLog.OperationStarted("Backend service host creation and startup", "Runtime");
        Batteries_V2.Init();

        var keyProtector = _options.KeyProtectorFactory()
            ?? throw new InvalidOperationException("The platform key-protector factory returned null.");
        var discoveryLease = _options.DiscoveryNetworkLeaseFactory()
            ?? throw new InvalidOperationException("The platform discovery-lease factory returned null.");

        BackendServiceHost host;
        if (_options.ServiceHostFactory is not null)
        {
            host = _options.ServiceHostFactory(keyProtector, discoveryLease)
                ?? throw new InvalidOperationException("The backend service-host factory returned null.");
        }
        else
        {
            var executionProfileProvider = _executionProfileProvider
                ?? throw new InvalidOperationException("The backend execution-profile provider has not been configured.");
            var services = new ServiceCollection();
            services.AddPasswordManagerLocalBackend(
                _options.StoragePaths,
                keyProtector,
                discoveryLease,
                executionProfileProvider);
            host = new BackendServiceHost(services.BuildServiceProvider());
        }
        try
        {
            BackendDebugLog.Info("Backend dependency container created successfully.", "Runtime");
            await host.Services
                .GetRequiredService<IBackendInitializationService>()
                .InitializeAsync(CancellationToken.None);
            BackendDebugLog.OperationCompleted("Backend persistent-state initialization", "Runtime");

            await host.StartAsync(CancellationToken.None);
            BackendDebugLog.OperationCompleted("Backend service host creation and startup", "Runtime");

            var syncRuntime = host.Services.GetRequiredService<ISyncRuntimeService>();
            return (host, syncRuntime);
        }
        catch (Exception exception)
        {
            BackendDebugLog.Error("Backend service host creation or startup failed.", exception, "Runtime");
            try
            {
                await host.DisposeAsync();
            }
            catch (Exception disposeException)
            {
                BackendDebugLog.Error(
                    "Backend service host cleanup after startup failure also failed.",
                    disposeException,
                    "Runtime");
            }

            throw;
        }
    }

    private async Task RunSyncStartupAsync(long generation)
    {
        await _lifecycleLock.WaitAsync(CancellationToken.None);
        try
        {
            ISyncRuntimeService? syncRuntime;
            lock (_gate)
            {
                if (_disposed ||
                    _hostGeneration != generation ||
                    _snapshot.State != BackendRuntimeState.Ready)
                {
                    return;
                }

                syncRuntime = _syncRuntime;
            }

            if (syncRuntime is null)
                return;

            try
            {
                await syncRuntime.RefreshSyncEnabledAsync(CancellationToken.None);
                BackendDebugLog.Info(
                    $"Synchronization runtime startup refresh completed successfully. State={syncRuntime.Snapshot.State}.",
                    "Runtime");
            }
            catch (Exception exception)
            {
                BackendDebugLog.Error(
                    "Synchronization runtime startup refresh failed and the runtime became degraded.",
                    exception,
                    "Runtime");
                SyncRuntimeStateChangedEventArgs? stateChange;
                lock (_gate)
                {
                    if (_syncRuntime != syncRuntime || _hostGeneration != generation)
                        return;

                    stateChange = SetSyncSnapshotLocked(
                        new SyncRuntimeSnapshot(SyncRuntimeState.Degraded, exception));
                }

                Publish(stateChange);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task RunStopAsync(TaskCompletionSource completion)
    {
        BackendRuntimeStateChangedEventArgs? stateChange = null;
        SyncRuntimeStateChangedEventArgs? syncStateChange = null;
        Exception? failure = null;

        await _lifecycleLock.WaitAsync(CancellationToken.None);
        try
        {
            try
            {
                await DisposeCurrentHostCoreAsync();
            }
            catch (Exception exception)
            {
                failure = exception;
                BackendDebugLog.Error("Backend shutdown failed.", exception, "Runtime");
            }

            lock (_gate)
            {
                _hostGeneration++;
                _host = null;
                _syncRuntime = null;
                syncStateChange = SetSyncSnapshotLocked(new SyncRuntimeSnapshot(SyncRuntimeState.Disabled, null));
                stateChange = failure is null
                    ? TransitionLocked(
                        BackendRuntimeState.Stopped,
                        BackendRuntimeFailureKind.None,
                        null)
                    : TransitionLocked(
                        BackendRuntimeState.Failed,
                        BackendRuntimeFailureKind.ShutdownFailure,
                        failure);
                _stopTask = null;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }

        Publish(syncStateChange);
        Publish(stateChange);

        if (failure is null)
        {
            BackendDebugLog.OperationCompleted("Backend shutdown", "Runtime");
            completion.TrySetResult();
        }
        else
        {
            BackendDebugLog.Error("Backend shutdown completed in a failed state.", failure, "Runtime");
            completion.TrySetException(failure);
        }
    }

    private async Task DisposeCurrentHostCoreAsync()
    {
        BackendServiceHost? host;
        ISyncRuntimeService? syncRuntime;

        lock (_gate)
        {
            host = _host;
            syncRuntime = _syncRuntime;
            if (syncRuntime is not null)
                syncRuntime.StateChanged -= HandleSyncStateChanged;

            _host = null;
            _syncRuntime = null;
            _hostGeneration++;
        }

        var failures = new List<Exception>();
        Exception? unsafeRestartFailure = null;
        try
        {
            await CloseInteractiveStateForShutdownAsync(host);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (syncRuntime is not null)
        {
            try
            {
                await syncRuntime.StopAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                unsafeRestartFailure = exception;
            }
        }

        if (host is not null)
        {
            try
            {
                await host.DisposeAsync();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                unsafeRestartFailure = unsafeRestartFailure is null
                    ? exception
                    : new AggregateException(unsafeRestartFailure, exception);
            }
        }

        if (unsafeRestartFailure is not null)
        {
            lock (_gate)
                _restartBlockedFailure ??= unsafeRestartFailure;
        }

        if (failures.Count == 1)
            throw failures[0];
        if (failures.Count > 1)
            throw new AggregateException(failures);
    }

    private void HandleSyncStateChanged(object? sender, SyncRuntimeStateChangedEventArgs args)
    {
        SyncRuntimeStateChangedEventArgs? stateChange;
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _syncRuntime))
                return;

            stateChange = SetSyncSnapshotLocked(args.Current);
        }

        Publish(stateChange);
    }

    private BackendRuntimeStateChangedEventArgs? TransitionLocked(
        BackendRuntimeState state,
        BackendRuntimeFailureKind failureKind,
        Exception? failure)
    {
        if (_snapshot.State == state &&
            _snapshot.FailureKind == failureKind &&
            ReferenceEquals(_snapshot.Failure, failure))
        {
            return null;
        }

        var previous = _snapshot;
        _snapshot = new BackendRuntimeSnapshot(
            state,
            failureKind,
            failure,
            DateTimeOffset.UtcNow);
        return new BackendRuntimeStateChangedEventArgs(previous, _snapshot);
    }

    private void MarkInteractiveSessionClosing(InteractiveBackendSession session)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_interactiveSession, session))
                _interactiveSession = null;

            if (_interactiveSessionSnapshot.State != InteractiveSessionLifecycleState.CleanupFailed)
                SetInteractiveSessionSnapshotLocked(InteractiveSessionLifecycleState.Closing, null);
        }
    }

    private void EnsureInteractiveSessionCanOpenLocked()
    {
        switch (_interactiveSessionSnapshot.State)
        {
            case InteractiveSessionLifecycleState.None:
                if (_interactiveSession is not null)
                    throw new InvalidOperationException("The interactive session registration is inconsistent.");
                return;
            case InteractiveSessionLifecycleState.CleanupFailed:
                throw new InvalidOperationException(
                    "Interactive session cleanup failed. A full backend runtime recovery is required before another session can open.",
                    _interactiveSessionSnapshot.Failure);
            default:
                throw new InvalidOperationException(
                    $"An interactive backend session cannot open while the lifecycle state is {_interactiveSessionSnapshot.State}.");
        }
    }

    private void SetInteractiveSessionSnapshotLocked(
        InteractiveSessionLifecycleState state,
        Exception? failure)
    {
        if (_interactiveSessionSnapshot.State == state &&
            ReferenceEquals(_interactiveSessionSnapshot.Failure, failure))
        {
            return;
        }

        _interactiveSessionSnapshot = new InteractiveSessionLifecycleSnapshot(
            state,
            failure,
            DateTimeOffset.UtcNow);
    }

    private SyncRuntimeStateChangedEventArgs? SetSyncSnapshotLocked(SyncRuntimeSnapshot snapshot)
    {
        if (_syncSnapshot == snapshot)
            return null;

        var previous = _syncSnapshot;
        _syncSnapshot = snapshot;
        return new SyncRuntimeStateChangedEventArgs(previous, snapshot);
    }

    private static (BackendRuntimeState State, BackendRuntimeFailureKind FailureKind) ClassifyFailure(
        Exception exception)
    {
        var primaryException = GetPrimaryFailure(exception);
        return primaryException switch
        {
            KeyProtectorUnavailableException =>
                (BackendRuntimeState.WaitingForDeviceUnlock, BackendRuntimeFailureKind.PlatformKeyUnavailable),
            DatabaseVersionNotSupportedException =>
                (BackendRuntimeState.Failed, BackendRuntimeFailureKind.DatabaseCompatibility),
            UnauthorizedAccessException or IOException =>
                (BackendRuntimeState.Failed, BackendRuntimeFailureKind.StorageUnavailable),
            _ =>
                (BackendRuntimeState.Failed, BackendRuntimeFailureKind.StartupFailure)
        };
    }

    private static Exception GetPrimaryFailure(Exception exception)
    {
        while (exception is AggregateException { InnerExceptions.Count: > 0 } aggregate)
            exception = aggregate.InnerExceptions[0];

        return exception;
    }

    private void ThrowIfRestartBlockedLocked()
    {
        if (_restartBlockedFailure is not null)
        {
            throw new InvalidOperationException(
                "The backend runtime cannot restart safely on this instance after an incomplete shutdown. Runtime recreation is required.",
                _restartBlockedFailure);
        }
    }

    private void ThrowIfDisposedLocked()
    {
        if (_disposeRequested || _disposed)
            throw new ObjectDisposedException(nameof(BackendRuntime));
    }

    private void Publish(BackendRuntimeStateChangedEventArgs? stateChange)
    {
        if (stateChange is null)
            return;

        var handlers = StateChanged;
        if (handlers is null)
            return;

        foreach (EventHandler<BackendRuntimeStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, stateChange);
            }
            catch
            {
            }
        }
    }

    private void Publish(SyncRuntimeStateChangedEventArgs? stateChange)
    {
        if (stateChange is null)
            return;

        var handlers = SyncStateChanged;
        if (handlers is null)
            return;

        foreach (EventHandler<SyncRuntimeStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, stateChange);
            }
            catch
            {
            }
        }
    }
}
