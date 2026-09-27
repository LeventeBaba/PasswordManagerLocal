using PasswordManagerLocal.Windows.Agent.Localization;
using PasswordManagerLocal.Windows.Agent.Preferences;
using PasswordManagerLocal.Windows.Agent.Backend;
using PasswordManagerLocal.Windows.Agent.Background;
using PasswordManagerLocal.Windows.Agent.Lifecycle;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Windows.Agent.Endpoint;
using PasswordManagerLocal.Windows.Agent.Tray;
using PasswordManagerLocal.Windows.Agent.Ui;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Coordination;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Server;
using System.Runtime.ExceptionServices;

namespace PasswordManagerLocal.Windows.Agent.Hosting;

public sealed class WindowsAgentHost : IWindowsAgentHost
{
    private readonly IProcessInstanceLock _processLock;
    private readonly IProcessInstanceLockProbe _uiProcessLockProbe;
    private readonly IWindowsAgentAdmissionGate _admissionGate;
    private readonly IWindowsIpcServerHost _controlServer;
    private readonly IWindowsAgentEndpointHost _endpointHost;
    private readonly IWindowsAgentBackendRuntimeOwner _backendOwner;
    private readonly IWindowsBackgroundSyncCoordinator _backgroundSyncCoordinator;
    private readonly WindowsAgentLifecycleTransitionCoordinator _lifecycleTransitions;
    private readonly ITrayIconController _trayIcon;
    private readonly IWindowsUiOpenService _uiOpenService;
    private readonly IWindowsUiCloseService _uiCloseService;
    private readonly IUiConnectionCoordinator _uiConnectionCoordinator;
    private readonly WindowsAgentShutdownCoordinator _shutdownCoordinator;
    private readonly WindowsAgentStateStore _stateStore;
    private readonly WindowsAgentProcessLifetimeCoordinator? _processLifetimeCoordinator;
    private readonly IAgentLocalizer _localizer;
    private readonly IAgentApplicationPreferencesReloadCoordinator? _applicationPreferencesReloadCoordinator;
    private readonly TimeSpan _uiRegistrationPreflightTimeout;
    private readonly TimeSpan _uiRegistrationPollInterval;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _shutdownCoordinationGate = new(1, 1);
    private readonly object _shutdownGate = new();
    private Task<WindowsAgentShutdownResult>? _shutdownTask;
    private Task<WindowsAgentShutdownResult>? _userExitTask;
    private int _started;
    private int _backendStartupAttempted;
    private int _controlServerStartupAttempted;
    private int _endpointHostStartupAttempted;
    private int _trayStartupAttempted;
    private int _shutdownRequested;
    private int _shutdownStarted;
    private int _disposeStarted;
    private int _retainProcessOwnershipUntilTermination;

    public WindowsAgentHost(
        IProcessInstanceLock processLock,
        IProcessInstanceLockProbe uiProcessLockProbe,
        IWindowsAgentAdmissionGate admissionGate,
        IWindowsIpcServerHost controlServer,
        IWindowsAgentEndpointHost endpointHost,
        IWindowsAgentBackendRuntimeOwner backendOwner,
        IWindowsBackgroundSyncCoordinator backgroundSyncCoordinator,
        WindowsAgentLifecycleTransitionCoordinator lifecycleTransitions,
        ITrayIconController trayIcon,
        IWindowsUiOpenService uiOpenService,
        IWindowsUiCloseService uiCloseService,
        IUiConnectionCoordinator uiConnectionCoordinator,
        WindowsAgentShutdownCoordinator shutdownCoordinator,
        WindowsAgentStateStore stateStore,
        IAgentLocalizer localizer,
        TimeSpan? uiRegistrationPreflightTimeout = null,
        TimeSpan? uiRegistrationPollInterval = null,
        WindowsAgentProcessLifetimeCoordinator? processLifetimeCoordinator = null,
        IAgentApplicationPreferencesReloadCoordinator? applicationPreferencesReloadCoordinator = null)
    {
        _processLock = processLock ?? throw new ArgumentNullException(nameof(processLock));
        _uiProcessLockProbe = uiProcessLockProbe
            ?? throw new ArgumentNullException(nameof(uiProcessLockProbe));
        _admissionGate = admissionGate ?? throw new ArgumentNullException(nameof(admissionGate));
        _controlServer = controlServer ?? throw new ArgumentNullException(nameof(controlServer));
        _endpointHost = endpointHost ?? throw new ArgumentNullException(nameof(endpointHost));
        _backendOwner = backendOwner ?? throw new ArgumentNullException(nameof(backendOwner));
        _backgroundSyncCoordinator = backgroundSyncCoordinator
            ?? throw new ArgumentNullException(nameof(backgroundSyncCoordinator));
        _lifecycleTransitions = lifecycleTransitions
            ?? throw new ArgumentNullException(nameof(lifecycleTransitions));
        _trayIcon = trayIcon ?? throw new ArgumentNullException(nameof(trayIcon));
        _uiOpenService = uiOpenService ?? throw new ArgumentNullException(nameof(uiOpenService));
        _uiCloseService = uiCloseService ?? throw new ArgumentNullException(nameof(uiCloseService));
        _uiConnectionCoordinator = uiConnectionCoordinator
            ?? throw new ArgumentNullException(nameof(uiConnectionCoordinator));
        _shutdownCoordinator = shutdownCoordinator ?? throw new ArgumentNullException(nameof(shutdownCoordinator));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _processLifetimeCoordinator = processLifetimeCoordinator;
        _applicationPreferencesReloadCoordinator = applicationPreferencesReloadCoordinator;
        _uiRegistrationPreflightTimeout = uiRegistrationPreflightTimeout ?? TimeSpan.FromSeconds(2);
        _uiRegistrationPollInterval = uiRegistrationPollInterval ?? TimeSpan.FromMilliseconds(100);
        if (_uiRegistrationPreflightTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(uiRegistrationPreflightTimeout));
        if (_uiRegistrationPollInterval <= TimeSpan.Zero ||
            _uiRegistrationPollInterval > _uiRegistrationPreflightTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(uiRegistrationPollInterval));
        }
        _shutdownCoordinator.ShutdownRequested += HandleShutdownRequested;
        _trayIcon.OpenRequested += HandleOpenRequested;
        _trayIcon.ExitRequested += HandleExitRequested;
        _backendOwner.StateChanged += HandleBackendOwnerStateChanged;
        _endpointHost.StateChanged += HandleEndpointHostStateChanged;
    }

    public bool RetainsProcessOwnershipUntilTermination =>
        Volatile.Read(ref _retainProcessOwnershipUntilTermination) != 0;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The Windows agent host has already started.");

        Exception? startupFailure = null;
        var ownershipUnavailable = false;
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            _processLock.EnsureOwnership();
            Interlocked.Exchange(ref _backendStartupAttempted, 1);
            await _backendOwner.StartAsync(cancellationToken);
            await _backgroundSyncCoordinator.InitializeAsync(cancellationToken);
            var initialBackgroundSyncState = await _backgroundSyncCoordinator.GetStateAsync(
                cancellationToken);
            Interlocked.Exchange(ref _controlServerStartupAttempted, 1);
            await _controlServer.StartAsync(cancellationToken);
            EnsureBackendOwnerReadyForEndpointStart();
            Interlocked.Exchange(ref _endpointHostStartupAttempted, 1);
            await _endpointHost.StartAsync(cancellationToken);
            Interlocked.Exchange(ref _trayStartupAttempted, 1);
            await _trayIcon.InitializeAsync(cancellationToken);
            await _trayIcon.SetVisibleAsync(
                initialBackgroundSyncState.IsEnabled,
                cancellationToken);
            if (Volatile.Read(ref _shutdownRequested) == 0 &&
                Volatile.Read(ref _shutdownStarted) == 0)
            {
                _stateStore.MarkRunning(DateTimeOffset.UtcNow);
                _admissionGate.Open();
                _processLifetimeCoordinator?.Start();
                _ = ObserveControlServerAsync();
                _ = ObserveEndpointHostAsync();
            }
        }
        catch (ProcessInstanceAlreadyOwnedException exception)
        {
            _admissionGate.ClosePermanently();
            _processLock.Dispose();
            Interlocked.Exchange(ref _shutdownStarted, 1);
            startupFailure = exception;
            ownershipUnavailable = true;
        }
        catch (Exception exception)
        {
            _admissionGate.ClosePermanently();
            _stateStore.MarkFailed(_localizer.GetString(AgentLocalizationKeys.RuntimeShellStartFailed));
            startupFailure = exception;
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (startupFailure is null)
            return;

        if (!ownershipUnavailable)
        {
            var shutdownResult = await RequestShutdownAsync(
                WindowsAgentShutdownReason.StartupFailure,
                CancellationToken.None);
            if (shutdownResult.Failure is not null)
                startupFailure = new AggregateException(startupFailure, shutdownResult.Failure);
        }

        ExceptionDispatchInfo.Capture(startupFailure).Throw();
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestShutdownAsync(
            WindowsAgentShutdownReason.ApplicationExit,
            cancellationToken);
        if (result.Kind == WindowsAgentShutdownResultKind.Failed && result.Failure is not null)
            ExceptionDispatchInfo.Capture(result.Failure).Throw();
    }

    public Task<WindowsAgentShutdownResult> RequestShutdownAsync(
        WindowsAgentShutdownReason reason,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));

        var task = reason == WindowsAgentShutdownReason.UserRequestedExit
            ? GetOrStartUserExitTask()
            : GetOrStartDestructiveShutdownTask(reason);
        return cancellationToken.CanBeCanceled
            ? task.WaitAsync(cancellationToken)
            : task;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        Exception? failure = null;
        try
        {
            await ShutdownAsync();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            _shutdownCoordinator.ShutdownRequested -= HandleShutdownRequested;
            _trayIcon.OpenRequested -= HandleOpenRequested;
            _trayIcon.ExitRequested -= HandleExitRequested;
            _backendOwner.StateChanged -= HandleBackendOwnerStateChanged;
            _endpointHost.StateChanged -= HandleEndpointHostStateChanged;
            _lifecycleGate.Dispose();
            _shutdownCoordinationGate.Dispose();
            _lifecycleTransitions.Dispose();
            GC.SuppressFinalize(this);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private Task<WindowsAgentShutdownResult> GetOrStartUserExitTask()
    {
        lock (_shutdownGate)
        {
            if (_shutdownTask is not null)
                return _shutdownTask;
            if (_userExitTask is not null)
                return _userExitTask;

            var userExitTask = CoordinateUserExitAsync();
            _userExitTask = userExitTask;
            _ = ClearCompletedUserExitTaskAsync(userExitTask);
            return userExitTask;
        }
    }

    private async Task ClearCompletedUserExitTaskAsync(
        Task<WindowsAgentShutdownResult> userExitTask)
    {
        var shouldClear = false;
        try
        {
            var result = await userExitTask;
            shouldClear = result.Kind == WindowsAgentShutdownResultKind.Rejected;
        }
        catch
        {
            shouldClear = true;
        }

        if (!shouldClear)
            return;

        lock (_shutdownGate)
        {
            if (ReferenceEquals(_userExitTask, userExitTask))
                _userExitTask = null;
        }
    }

    private async Task<WindowsAgentShutdownResult> CoordinateUserExitAsync()
    {
        Task<WindowsAgentShutdownResult>? destructiveShutdown = null;
        WindowsAgentLifecycleTransitionLease? transition = null;
        var transitionOwnsShutdown = false;
        await _shutdownCoordinationGate.WaitAsync();
        try
        {
            if (_stateStore.State != AgentState.Running || !_admissionGate.IsOpen)
            {
                destructiveShutdown = GetExistingShutdownTask();
                if (destructiveShutdown is null)
                {
                    return new WindowsAgentShutdownResult(
                        WindowsAgentShutdownResultKind.Rejected,
                        _localizer.GetString(AgentLocalizationKeys.ExitNotAvailable));
                }
            }
            else
            {
                transition = await _lifecycleTransitions.EnterAsync(
                    WindowsAgentLifecycleTransitionState.ShuttingDown,
                    CancellationToken.None);
                if (_stateStore.State != AgentState.Running || !_admissionGate.IsOpen)
                {
                    destructiveShutdown = GetExistingShutdownTask();
                    if (destructiveShutdown is null)
                    {
                        return new WindowsAgentShutdownResult(
                            WindowsAgentShutdownResultKind.Rejected,
                            _localizer.GetString(AgentLocalizationKeys.ExitNoLongerAvailable));
                    }
                }
                else
                {
                    var preflight = await ResolveTrayExitRegistrationAsync();
                    if (!preflight.CanProceed)
                    {
                        await ShowExitFailureAsync(preflight.SafeMessage!);
                        return new WindowsAgentShutdownResult(
                            WindowsAgentShutdownResultKind.Rejected,
                            preflight.SafeMessage);
                    }

                    if (!_uiConnectionCoordinator.TryBeginIntentionalShutdown(out var registration))
                    {
                        var safeMessage = _localizer.GetString(AgentLocalizationKeys.ExitAlreadyInProgress);
                        await ShowExitFailureAsync(safeMessage);
                        return new WindowsAgentShutdownResult(
                            WindowsAgentShutdownResultKind.Rejected,
                            safeMessage);
                    }

                    if (registration is null)
                    {
                        var frozenProbe = _uiProcessLockProbe.Probe();
                        if (frozenProbe != ProcessInstanceLockProbeResult.Free)
                        {
                            _uiConnectionCoordinator.CancelIntentionalShutdown();
                            var safeMessage = _localizer.GetString(AgentLocalizationKeys.ExitUiReconnecting);
                            await ShowExitFailureAsync(safeMessage);
                            return new WindowsAgentShutdownResult(
                                WindowsAgentShutdownResultKind.Rejected,
                                safeMessage);
                        }
                    }
                    else
                    {
                        WindowsUiCloseResult closeResult;
                        try
                        {
                            closeResult = await _uiCloseService.RequestIntentionalShutdownAsync();
                        }
                        catch (Exception exception)
                        {
                            closeResult = new WindowsUiCloseResult(
                                WindowsUiCloseResultKind.Failed,
                                _localizer.GetString(AgentLocalizationKeys.ExitUiAcknowledgementFailed));
                            _uiConnectionCoordinator.CancelIntentionalShutdown();
                            await ShowExitFailureAsync(closeResult.SafeMessage);
                            return new WindowsAgentShutdownResult(
                                WindowsAgentShutdownResultKind.Rejected,
                                closeResult.SafeMessage,
                                exception);
                        }

                        if (!closeResult.IsAcknowledged)
                        {
                            _uiConnectionCoordinator.CancelIntentionalShutdown();
                            await ShowExitFailureAsync(closeResult.SafeMessage);
                            return new WindowsAgentShutdownResult(
                                WindowsAgentShutdownResultKind.Rejected,
                                closeResult.SafeMessage);
                        }
                    }

                    Interlocked.Exchange(ref _shutdownRequested, 1);
                    _admissionGate.ClosePermanently();
                    lock (_shutdownGate)
                    {
                        if (_shutdownTask is null)
                        {
                            destructiveShutdown = Task.Run(
                                () => ShutdownCoreAsync(
                                    WindowsAgentShutdownReason.UserRequestedExit));
                            _shutdownTask = destructiveShutdown;
                            transitionOwnsShutdown = true;
                        }
                        else
                        {
                            destructiveShutdown = _shutdownTask;
                        }
                    }
                }
            }
        }
        finally
        {
            if (!transitionOwnsShutdown && transition is not null)
                await transition.DisposeAsync();
            _shutdownCoordinationGate.Release();
        }

        var shutdown = destructiveShutdown ?? throw new InvalidOperationException(
            "The coordinated shutdown task was not created.");
        if (!transitionOwnsShutdown)
            return await shutdown;

        try
        {
            return await shutdown;
        }
        finally
        {
            await transition!.DisposeAsync();
        }
    }

    private Task<WindowsAgentShutdownResult>? GetExistingShutdownTask()
    {
        lock (_shutdownGate)
            return _shutdownTask;
    }

    private Task<WindowsAgentShutdownResult> GetOrStartDestructiveShutdownTask(
        WindowsAgentShutdownReason reason)
    {
        Interlocked.Exchange(ref _shutdownRequested, 1);
        _admissionGate.ClosePermanently();
        lock (_shutdownGate)
            return _shutdownTask ??= Task.Run(
                () => CoordinateDestructiveShutdownAsync(reason));
    }

    private async Task<WindowsAgentShutdownResult> CoordinateDestructiveShutdownAsync(
        WindowsAgentShutdownReason reason)
    {
        await _shutdownCoordinationGate.WaitAsync();
        try
        {
            await using var transition = await _lifecycleTransitions.EnterAsync(
                WindowsAgentLifecycleTransitionState.ShuttingDown,
                CancellationToken.None);
            return await ShutdownCoreAsync(reason);
        }
        finally
        {
            _shutdownCoordinationGate.Release();
        }
    }

    private async Task<WindowsAgentShutdownResult> ShutdownCoreAsync(
        WindowsAgentShutdownReason reason)
    {
        Interlocked.Exchange(ref _shutdownRequested, 1);
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return new WindowsAgentShutdownResult(
                WindowsAgentShutdownResultKind.Completed);
        }

        _admissionGate.ClosePermanently();
        await _lifecycleGate.WaitAsync();
        try
        {
            _stateStore.MarkStopping();
            var criticalFailures = new List<Exception>();
            var noncriticalFailures = new List<Exception>();

            async Task CaptureCriticalAsync(Func<Task> cleanup)
            {
                try
                {
                    await cleanup();
                }
                catch (Exception exception)
                {
                    criticalFailures.Add(exception);
                }
            }

            async Task CaptureNoncriticalAsync(Func<Task> cleanup)
            {
                try
                {
                    await cleanup();
                }
                catch (Exception exception)
                {
                    noncriticalFailures.Add(exception);
                }
            }

            if (_processLifetimeCoordinator is not null)
            {
                await CaptureNoncriticalAsync(
                    async () => await _processLifetimeCoordinator.DisposeAsync());
            }

            if (Volatile.Read(ref _endpointHostStartupAttempted) != 0)
                await CaptureCriticalAsync(() => _endpointHost.StopAsync());

            await CaptureCriticalAsync(() => _admissionGate.WaitForDrainAsync(CancellationToken.None));

            if (Volatile.Read(ref _backendStartupAttempted) != 0)
                await CaptureCriticalAsync(() => _backgroundSyncCoordinator.ShutdownAsync());

            if (Volatile.Read(ref _backendStartupAttempted) != 0)
                await CaptureCriticalAsync(() => _backendOwner.StopAsync());

            if (Volatile.Read(ref _controlServerStartupAttempted) != 0)
                await CaptureCriticalAsync(() => _controlServer.StopAsync());

            if (Volatile.Read(ref _backendStartupAttempted) != 0)
                await CaptureCriticalAsync(async () => await _backendOwner.DisposeAsync());

            if (Volatile.Read(ref _endpointHostStartupAttempted) != 0)
                await CaptureCriticalAsync(async () => await _endpointHost.DisposeAsync());

            if (Volatile.Read(ref _controlServerStartupAttempted) != 0)
                await CaptureCriticalAsync(async () => await _controlServer.DisposeAsync());

            if (_applicationPreferencesReloadCoordinator is not null)
            {
                await CaptureNoncriticalAsync(
                    async () => await _applicationPreferencesReloadCoordinator.DisposeAsync());
            }

            if (Volatile.Read(ref _trayStartupAttempted) != 0)
                await CaptureNoncriticalAsync(async () => await _trayIcon.DisposeAsync());

            var reasonRetainsOwnership = reason is
                WindowsAgentShutdownReason.RestartRequired or
                WindowsAgentShutdownReason.FatalLifecycleFailure;
            if (criticalFailures.Count != 0 || reasonRetainsOwnership)
            {
                // The host keeps the authoritative lock strongly referenced until process termination.
                Interlocked.Exchange(ref _retainProcessOwnershipUntilTermination, 1);
            }
            else
            {
                try
                {
                    _processLock.Dispose();
                }
                catch (Exception exception)
                {
                    criticalFailures.Add(exception);
                    Interlocked.Exchange(ref _retainProcessOwnershipUntilTermination, 1);
                }
            }

            if (criticalFailures.Count == 0 && noncriticalFailures.Count == 0)
            {
                _stateStore.MarkStopped();
                return new WindowsAgentShutdownResult(
                    WindowsAgentShutdownResultKind.Completed);
            }

            var allFailures = criticalFailures.Concat(noncriticalFailures).ToArray();
            var failure = allFailures.Length == 1
                ? allFailures[0]
                : new AggregateException(allFailures);
            _stateStore.MarkShutdownFailed(
                criticalFailures.Count == 0
                    ? _localizer.GetString(AgentLocalizationKeys.RuntimeShutdownShellCleanupFailed)
                    : _localizer.GetString(AgentLocalizationKeys.RuntimeShutdownResourcesFailed),
                requiresProcessRestart: criticalFailures.Count != 0);
            return new WindowsAgentShutdownResult(
                WindowsAgentShutdownResultKind.Failed,
                _stateStore.LastFailure?.SafeMessage,
                failure);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void EnsureBackendOwnerReadyForEndpointStart()
    {
        var snapshot = _backendOwner.Snapshot;
        var recoverableDatabaseFailure =
            snapshot.State == WindowsAgentBackendOwnerState.Failed &&
            snapshot.Runtime.FailureKind == BackendRuntimeFailureKind.DatabaseCompatibility;
        if ((snapshot.State != WindowsAgentBackendOwnerState.Ready && !recoverableDatabaseFailure) ||
            snapshot.IsResetting ||
            snapshot.RequiresProcessRestart)
        {
            throw new InvalidOperationException(
                "The Windows agent endpoint listener cannot start before the backend owner is ready.");
        }
    }

    private async Task ObserveControlServerAsync()
    {
        try { await _controlServer.Completion; } catch { }
        if (Volatile.Read(ref _shutdownStarted) == 0 &&
            _controlServer.ListenerFailure is not null)
        {
            _admissionGate.ClosePermanently();
            _stateStore.MarkFailed(_localizer.GetString(AgentLocalizationKeys.RuntimeControlListenerFailed));
            _shutdownCoordinator.RequestShutdown(WindowsAgentShutdownReason.FatalLifecycleFailure);
        }
    }

    private async Task ObserveEndpointHostAsync()
    {
        try { await _endpointHost.Completion; } catch { }
        if (Volatile.Read(ref _shutdownStarted) == 0 &&
            _endpointHost.Snapshot.State == WindowsAgentEndpointHostState.Failed)
        {
            _admissionGate.ClosePermanently();
            _stateStore.MarkFailed(_localizer.GetString(AgentLocalizationKeys.RuntimeEndpointListenerFailed));
            _shutdownCoordinator.RequestShutdown(WindowsAgentShutdownReason.FatalLifecycleFailure);
        }
    }

    private void HandleBackendOwnerStateChanged(object? sender, EventArgs args)
    {
        var snapshot = _backendOwner.Snapshot;
        if (snapshot.RequiresProcessRestart)
        {
            _admissionGate.ClosePermanently();
            _shutdownCoordinator.RequestShutdown(WindowsAgentShutdownReason.RestartRequired);
            return;
        }

        if (_stateStore.State == AgentState.Running &&
            snapshot.State == WindowsAgentBackendOwnerState.Failed &&
            snapshot.Runtime.FailureKind != BackendRuntimeFailureKind.DatabaseCompatibility &&
            Volatile.Read(ref _shutdownStarted) == 0)
        {
            _admissionGate.ClosePermanently();
            _stateStore.MarkFailed(_localizer.GetString(AgentLocalizationKeys.RuntimeBackendFailed));
            _shutdownCoordinator.RequestShutdown(WindowsAgentShutdownReason.FatalLifecycleFailure);
        }
    }

    private void HandleEndpointHostStateChanged(object? sender, EventArgs args)
    {
        if (_endpointHost.Snapshot.State == WindowsAgentEndpointHostState.Failed &&
            Volatile.Read(ref _shutdownStarted) == 0)
        {
            _admissionGate.ClosePermanently();
            _stateStore.MarkFailed(_localizer.GetString(AgentLocalizationKeys.RuntimeEndpointListenerFailed));
            _shutdownCoordinator.RequestShutdown(WindowsAgentShutdownReason.FatalLifecycleFailure);
        }
    }

    private void HandleShutdownRequested(
        object? sender,
        WindowsAgentShutdownRequestedEventArgs args) =>
        _ = ObserveRequestedShutdownAsync(args.Reason);

    private void HandleExitRequested(object? sender, EventArgs args) =>
        _ = Task.Run(ObserveTrayExitAsync);

    private void HandleOpenRequested(object? sender, EventArgs args) =>
        _ = OpenUiFromTrayAsync();

    private async Task ObserveRequestedShutdownAsync(WindowsAgentShutdownReason reason)
    {
        try
        {
            await RequestShutdownAsync(reason);
        }
        catch
        {
        }
    }

    private async Task ObserveTrayExitAsync()
    {
        try
        {
            await RequestShutdownAsync(WindowsAgentShutdownReason.UserRequestedExit);
        }
        catch
        {
        }
    }

    private async Task ShowExitFailureAsync(string safeMessage)
    {
        try
        {
            await _trayIcon.ShowExitFailureAsync(safeMessage);
        }
        catch
        {
        }
    }

    private async Task<TrayExitRegistrationPreflight> ResolveTrayExitRegistrationAsync()
    {
        if (_uiConnectionCoordinator.Registration is not null)
            return TrayExitRegistrationPreflight.Proceed;

        var probe = _uiProcessLockProbe.Probe();
        if (probe == ProcessInstanceLockProbeResult.Uncertain)
        {
            return TrayExitRegistrationPreflight.Reject(
                _localizer.GetString(AgentLocalizationKeys.ExitUiPresenceUnknown));
        }
        if (probe == ProcessInstanceLockProbeResult.Free)
            return TrayExitRegistrationPreflight.Proceed;

        using var timeoutSource = new CancellationTokenSource(_uiRegistrationPreflightTimeout);
        try
        {
            while (_uiConnectionCoordinator.Registration is null)
            {
                await Task.Delay(_uiRegistrationPollInterval, timeoutSource.Token);
                if (_uiProcessLockProbe.Probe() == ProcessInstanceLockProbeResult.Free)
                    return TrayExitRegistrationPreflight.Proceed;
            }

            return TrayExitRegistrationPreflight.Proceed;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            return TrayExitRegistrationPreflight.Reject(
                _localizer.GetString(AgentLocalizationKeys.ExitUiReconnecting));
        }
    }

    private async Task OpenUiFromTrayAsync()
    {
        try { await _uiOpenService.OpenAsync(UiActivationReason.TrayIcon); } catch { }
    }
}
