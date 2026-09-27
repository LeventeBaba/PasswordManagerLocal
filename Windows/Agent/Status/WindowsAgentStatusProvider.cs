using PasswordManagerLocal.Windows.Agent.Localization;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Contracts.Errors;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Windows.Agent.Backend;
using PasswordManagerLocal.Windows.Agent.Background;
using PasswordManagerLocal.Windows.Agent.DatabaseReset;
using PasswordManagerLocal.Windows.Agent.Endpoint;
using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Agent.Status;

public sealed class WindowsAgentStatusProvider : IWindowsIpcStatusProvider
{
    private readonly IWindowsAgentStateSource _stateSource;
    private readonly IWindowsAgentAdmissionGate _admissionGate;
    private readonly IUiConnectionCoordinator _uiCoordinator;
    private readonly IWindowsBackgroundSyncCoordinator _backgroundSyncCoordinator;
    private readonly IWindowsAgentBackendRuntimeOwner _backendOwner;
    private readonly IWindowsAgentEndpointHost _endpointHost;
    private readonly AgentInteractiveEndpointAdapter _endpointAdapter;
    private readonly IWindowsAgentDatabaseResetCoordinator _resetCoordinator;
    private readonly IAgentLocalizer _localizer;

    public WindowsAgentStatusProvider(
        IWindowsAgentStateSource stateSource,
        IWindowsAgentAdmissionGate admissionGate,
        IUiConnectionCoordinator uiCoordinator,
        IWindowsBackgroundSyncCoordinator backgroundSyncCoordinator,
        IWindowsAgentBackendRuntimeOwner backendOwner,
        IWindowsAgentEndpointHost endpointHost,
        AgentInteractiveEndpointAdapter endpointAdapter,
        IWindowsAgentDatabaseResetCoordinator resetCoordinator,
        IAgentLocalizer localizer)
    {
        _stateSource = stateSource ?? throw new ArgumentNullException(nameof(stateSource));
        _admissionGate = admissionGate ?? throw new ArgumentNullException(nameof(admissionGate));
        _uiCoordinator = uiCoordinator ?? throw new ArgumentNullException(nameof(uiCoordinator));
        _backgroundSyncCoordinator = backgroundSyncCoordinator
            ?? throw new ArgumentNullException(nameof(backgroundSyncCoordinator));
        _backendOwner = backendOwner ?? throw new ArgumentNullException(nameof(backendOwner));
        _endpointHost = endpointHost ?? throw new ArgumentNullException(nameof(endpointHost));
        _endpointAdapter = endpointAdapter ?? throw new ArgumentNullException(nameof(endpointAdapter));
        _resetCoordinator = resetCoordinator ?? throw new ArgumentNullException(nameof(resetCoordinator));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    public async Task<AgentStatusDto> GetAgentStatusAsync(CancellationToken cancellationToken)
    {
        var backgroundState = await _backgroundSyncCoordinator.GetStateAsync(cancellationToken);

        var owner = _backendOwner.Snapshot;
        var runtimeRunning = owner.Runtime.State is
            BackendRuntimeState.Starting or
            BackendRuntimeState.Ready or
            BackendRuntimeState.WaitingForDeviceUnlock or
            BackendRuntimeState.Stopping;
        var shellRequiresRestart = _stateSource.LastFailure?.RequiresProcessRestart == true;
        var requiresProcessRestart = owner.RequiresProcessRestart || shellRequiresRestart;
        var failure = owner.RequiresProcessRestart
            ? CreateFailure(
                IpcFailureKind.Runtime,
                _localizer.GetString(AgentLocalizationKeys.RuntimeBackendRestartRequired),
                retryable: true,
                requiresRestart: true)
            : _stateSource.LastFailure;
        var endpointReady = _stateSource.State == AgentState.Running &&
            _admissionGate.IsOpen &&
            _endpointHost.Snapshot.State == WindowsAgentEndpointHostState.Ready &&
            !owner.IsResetting &&
            !requiresProcessRestart;

        return new AgentStatusDto(
            AgentState: _stateSource.State,
            AdmissionState: _admissionGate.State,
            IsUiConnected: _uiCoordinator.RegisteredConnectionId is not null,
            BackendOwnedByAgent: true,
            IsBackendRunning: runtimeRunning,
            IsBackgroundSyncEnabled: backgroundState.IsEnabled,
            RequiresProcessRestart: requiresProcessRestart,
            LastFailure: failure,
            StartedAtUtc: _stateSource.StartedAtUtc,
            IsEndpointHostReady: endpointReady,
            IsDatabaseResetInProgress: _resetCoordinator.IsResetting,
            HasInteractiveUiLease: (owner.ActiveReasons & BackendLifetimeReason.InteractiveUi) != 0,
            HasBackgroundSyncLease: backgroundState.IsBackgroundLeaseActive);
    }

    public Task<BackendRuntimeStatusDto> GetBackendRuntimeStatusAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owner = _backendOwner.Snapshot;
        if (owner.RequiresProcessRestart)
        {
            var failure = CreateFailure(
                IpcFailureKind.Runtime,
                _localizer.GetString(AgentLocalizationKeys.RuntimeBackendRecreationUnsafe),
                retryable: true,
                requiresRestart: true);
            return Task.FromResult(new BackendRuntimeStatusDto(
                BackendRuntimeStatusState.Failed,
                BackendRuntimeFailureStatusKind.ShutdownFailure,
                failure,
                RequiresProcessRestart: true,
                ChangedAtUtc: owner.ChangedAtUtc,
                DatabaseCompatibility: null));
        }

        var ownerFailed = owner.State == WindowsAgentBackendOwnerState.Failed;
        var state = ownerFailed
            ? BackendRuntimeStatusState.Failed
            : MapRuntimeState(owner.Runtime.State);
        var kind = MapRuntimeFailureKind(owner.Runtime.FailureKind);
        if (ownerFailed && kind == BackendRuntimeFailureStatusKind.None)
            kind = BackendRuntimeFailureStatusKind.StartupFailure;
        var failureDto = ownerFailed || owner.Runtime.Failure is not null
            ? CreateRuntimeFailure(kind, requiresRestart: false)
            : null;
        return Task.FromResult(new BackendRuntimeStatusDto(
            state,
            kind,
            failureDto,
            RequiresProcessRestart: false,
            ChangedAtUtc: owner.Runtime.ChangedAtUtc,
            DatabaseCompatibility: CreateDatabaseCompatibilityStatus(
                kind,
                owner.Runtime.Failure)));
    }

    public Task<InteractiveSessionStatusDto> GetInteractiveSessionStatusAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owner = _backendOwner.Snapshot;
        var state = owner.InteractiveSession.State switch
        {
            InteractiveSessionLifecycleState.None => InteractiveSessionStatusState.None,
            InteractiveSessionLifecycleState.Opening => InteractiveSessionStatusState.Opening,
            InteractiveSessionLifecycleState.Active =>
                _endpointAdapter.IsClosing
                    ? InteractiveSessionStatusState.Closing
                    : InteractiveSessionStatusState.Active,
            InteractiveSessionLifecycleState.Closing => InteractiveSessionStatusState.Closing,
            InteractiveSessionLifecycleState.CleanupFailed => InteractiveSessionStatusState.CleanupFailed,
            _ => InteractiveSessionStatusState.None
        };
        var cleanupFailure = state == InteractiveSessionStatusState.CleanupFailed
            ? CreateFailure(
                IpcFailureKind.InteractiveCleanup,
                _localizer.GetString(AgentLocalizationKeys.RuntimeInteractiveCleanupFailed),
                retryable: false,
                requiresRestart: owner.RequiresProcessRestart)
            : null;

        return Task.FromResult(new InteractiveSessionStatusDto(
            state,
            AcceptsNewOperations: state == InteractiveSessionStatusState.Active &&
                _endpointAdapter.AcceptsNewOperations,
            ActiveOperationCount: _endpointAdapter.ActiveOperationCount,
            CleanupFailure: cleanupFailure,
            ChangedAtUtc: owner.InteractiveSession.ChangedAtUtc));
    }

    public Task<SynchronizationStatusDto> GetSynchronizationStatusAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sync = _backendOwner.Snapshot.Synchronization;
        var state = sync.State switch
        {
            SyncRuntimeState.Disabled => SynchronizationStatusState.Disabled,
            SyncRuntimeState.Starting => SynchronizationStatusState.Starting,
            SyncRuntimeState.Running => SynchronizationStatusState.Running,
            SyncRuntimeState.Stopping => SynchronizationStatusState.Stopping,
            SyncRuntimeState.Degraded => SynchronizationStatusState.Degraded,
            _ => SynchronizationStatusState.Unavailable
        };
        var failure = state == SynchronizationStatusState.Degraded
            ? CreateFailure(
                IpcFailureKind.Synchronization,
                _localizer.GetString(AgentLocalizationKeys.RuntimeBackgroundSyncDegraded),
                retryable: true,
                requiresRestart: false)
            : null;
        return Task.FromResult(new SynchronizationStatusDto(state, failure));
    }

    private BackendRuntimeStatusState MapRuntimeState(BackendRuntimeState state) => state switch
    {
        BackendRuntimeState.NotStarted => BackendRuntimeStatusState.NotStarted,
        BackendRuntimeState.Starting => BackendRuntimeStatusState.Starting,
        BackendRuntimeState.Ready => BackendRuntimeStatusState.Ready,
        BackendRuntimeState.WaitingForDeviceUnlock => BackendRuntimeStatusState.WaitingForDeviceUnlock,
        BackendRuntimeState.Failed => BackendRuntimeStatusState.Failed,
        BackendRuntimeState.Stopping => BackendRuntimeStatusState.Stopping,
        BackendRuntimeState.Stopped => BackendRuntimeStatusState.Stopped,
        _ => BackendRuntimeStatusState.Unavailable
    };

    private static BackendRuntimeFailureStatusKind MapRuntimeFailureKind(
        BackendRuntimeFailureKind kind) => kind switch
    {
        BackendRuntimeFailureKind.None => BackendRuntimeFailureStatusKind.None,
        BackendRuntimeFailureKind.DatabaseCompatibility => BackendRuntimeFailureStatusKind.DatabaseCompatibility,
        BackendRuntimeFailureKind.PlatformKeyUnavailable => BackendRuntimeFailureStatusKind.PlatformKeyUnavailable,
        BackendRuntimeFailureKind.StorageUnavailable => BackendRuntimeFailureStatusKind.StorageUnavailable,
        BackendRuntimeFailureKind.StartupFailure => BackendRuntimeFailureStatusKind.StartupFailure,
        BackendRuntimeFailureKind.InteractiveCleanupFailure => BackendRuntimeFailureStatusKind.InteractiveCleanupFailure,
        BackendRuntimeFailureKind.ShutdownFailure => BackendRuntimeFailureStatusKind.ShutdownFailure,
        _ => BackendRuntimeFailureStatusKind.StartupFailure
    };

    private static DatabaseCompatibilityStatusDto? CreateDatabaseCompatibilityStatus(
        BackendRuntimeFailureStatusKind kind,
        Exception? failure)
    {
        if (kind != BackendRuntimeFailureStatusKind.DatabaseCompatibility)
            return null;

        var compatibilityFailure = FindDatabaseCompatibilityFailure(failure);
        return new DatabaseCompatibilityStatusDto(
            compatibilityFailure?.DetectedVersion,
            compatibilityFailure?.OldestSupportedVersion ?? DatabaseConstants.OldestSupportedDbVersion,
            compatibilityFailure?.CurrentVersion ?? DatabaseConstants.CurrentDbVersion);
    }

    private static DatabaseVersionNotSupportedException? FindDatabaseCompatibilityFailure(
        Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is DatabaseVersionNotSupportedException compatibilityFailure)
                return compatibilityFailure;

            if (exception is AggregateException aggregateException)
            {
                foreach (var innerException in aggregateException.Flatten().InnerExceptions)
                {
                    var nestedFailure = FindDatabaseCompatibilityFailure(innerException);
                    if (nestedFailure is not null)
                        return nestedFailure;
                }
            }

            exception = exception.InnerException;
        }

        return null;
    }

    private IpcFailureDto CreateRuntimeFailure(
        BackendRuntimeFailureStatusKind kind,
        bool requiresRestart) => kind switch
    {
        BackendRuntimeFailureStatusKind.PlatformKeyUnavailable => CreateFailure(
            IpcFailureKind.PlatformKey,
            _localizer.GetString(AgentLocalizationKeys.RuntimePlatformKeyUnavailable),
            retryable: true,
            requiresRestart),
        BackendRuntimeFailureStatusKind.StorageUnavailable => CreateFailure(
            IpcFailureKind.Storage,
            _localizer.GetString(AgentLocalizationKeys.RuntimeStorageUnavailable),
            retryable: true,
            requiresRestart),
        BackendRuntimeFailureStatusKind.InteractiveCleanupFailure => CreateFailure(
            IpcFailureKind.InteractiveCleanup,
            _localizer.GetString(AgentLocalizationKeys.RuntimeInteractiveCleanupFailed),
            retryable: false,
            requiresRestart),
        _ => CreateFailure(
            IpcFailureKind.Runtime,
            kind == BackendRuntimeFailureStatusKind.DatabaseCompatibility
                ? _localizer.GetString(AgentLocalizationKeys.RuntimeDatabaseVersionUnsupported)
                : _localizer.GetString(AgentLocalizationKeys.RuntimeBackendFailed),
            retryable: kind != BackendRuntimeFailureStatusKind.DatabaseCompatibility,
            requiresRestart)
    };

    private static IpcFailureDto CreateFailure(
        IpcFailureKind kind,
        string message,
        bool retryable,
        bool requiresRestart) => new(
            kind,
            message,
            DateTimeOffset.UtcNow,
            retryable,
            requiresRestart);
}
