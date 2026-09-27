using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;

namespace PasswordManagerLocal.Android.Runtime;

public sealed class AndroidRuntimeServiceHost : IAsyncDisposable
{
    private readonly IAndroidRuntimeCompositionFactory _compositionFactory;
    private readonly IBackgroundSyncSettingsStore _settingsStore;
    private readonly IAndroidForegroundServiceController _foregroundServiceController;
    private readonly IAndroidSecureStorageAvailability _secureStorageAvailability;
    private readonly SemaphoreSlim _transitionLock = new(1, 1);
    private BackendRuntimeComposition? _composition;
    private IBackendRuntimeLease? _backgroundLease;
    private AndroidServiceFrontendBackendClient? _interactiveClient;
    private AndroidBackgroundSyncServiceState _backgroundState;
    private bool _settingsLoaded;
    private long _attachmentGeneration;
    private volatile bool _runtimeUnsafe;
    private volatile bool _disposed;
    private AndroidNotificationAvailability _notificationAvailability = AndroidNotificationAvailability.Unknown;

    public AndroidRuntimeServiceHost(
        IAndroidRuntimeCompositionFactory compositionFactory,
        IBackgroundSyncSettingsStore settingsStore,
        IAndroidForegroundServiceController foregroundServiceController,
        IAndroidSecureStorageAvailability secureStorageAvailability)
    {
        _compositionFactory = compositionFactory
            ?? throw new ArgumentNullException(nameof(compositionFactory));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _foregroundServiceController = foregroundServiceController
            ?? throw new ArgumentNullException(nameof(foregroundServiceController));
        _secureStorageAvailability = secureStorageAvailability
            ?? throw new ArgumentNullException(nameof(secureStorageAvailability));
        _backgroundState = CreateInitialState();
    }

    public AndroidRuntimeServiceSnapshot Snapshot
    {
        get
        {
            var composition = _composition;
            return new AndroidRuntimeServiceSnapshot(
                composition is not null,
                _interactiveClient is not null,
                _backgroundLease is not null,
                _backgroundState.IsForegroundActive,
                _disposed,
                _runtimeUnsafe,
                !_disposed && !_runtimeUnsafe,
                composition?.LifetimeCoordinator.ActiveReasons ?? BackendLifetimeReason.None,
                _backgroundState.ServiceStartPhase,
                IsRuntimeReady: composition is not null &&
                    (composition.LifetimeCoordinator.ActiveReasons &
                        (BackendLifetimeReason.InteractiveUi | BackendLifetimeReason.BackgroundSync)) != 0,
                _backgroundState.RequiresUserAction,
                _backgroundState.NotificationAvailability);
        }
    }

    public async Task<AndroidBackgroundSyncServiceState> RestoreBackgroundStateAsync(CancellationToken cancellationToken = default)
    {
        await _transitionLock.WaitAsync(cancellationToken);
        try
        {
            if (_runtimeUnsafe)
                return _backgroundState;

            ThrowIfDisposed();
            if (!_secureStorageAvailability.IsAvailable)
            {
                await EnterDeferredUntilUnlockLockedAsync();
                return _backgroundState;
            }

            await LoadSettingsLockedAsync(cancellationToken);
            if (_backgroundState.FailureKind == AndroidBackgroundSyncFailureKind.SettingRead)
            {
                var settingReadFailure = _backgroundState;
                try
                {
                    await DisableBackgroundRuntimeLockedAsync(requestServiceStop: true);
                    _backgroundState = settingReadFailure with
                    {
                        IsBackgroundLeaseActive = false,
                        IsForegroundActive = false,
                        ServiceStartPhase = AndroidServiceStartPhase.RuntimeStartupFailed,
                        IsRuntimeReady = false,
                        NotificationAvailability = AndroidNotificationAvailability.Unknown
                    };
                }
                catch
                {
                    _backgroundState = CreateFailureState(
                        false,
                        AndroidBackgroundSyncFailureKind.Shutdown,
                        "Background synchronization settings are unavailable and cleanup did not complete cleanly.",
                        AndroidServiceStartPhase.RuntimeStartupFailed);
                }

                return _backgroundState;
            }

            if (!_backgroundState.IsEnabled)
            {
                await DisableBackgroundRuntimeLockedAsync(requestServiceStop: true);
                _backgroundState = CreateOperationalState(false);
                return _backgroundState;
            }

            try
            {
                await EnsureBackgroundRuntimeLockedAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                var startupFailureState = _backgroundState;
                var cleanupFailed = false;
                try
                {
                    await DisableBackgroundRuntimeLockedAsync(requestServiceStop: true);
                }
                catch
                {
                    cleanupFailed = true;
                }

                if (startupFailureState.FailureKind == AndroidBackgroundSyncFailureKind.ForegroundService)
                {
                    _notificationAvailability = startupFailureState.NotificationAvailability;
                    _backgroundState = startupFailureState with
                    {
                        IsEnabled = true,
                        IsBackgroundLeaseActive = false,
                        IsForegroundActive = false,
                        IsRuntimeReady = false
                    };
                }
                else
                {
                    _backgroundState = CreateFailureState(
                        true,
                        cleanupFailed
                            ? AndroidBackgroundSyncFailureKind.Shutdown
                            : AndroidBackgroundSyncFailureKind.RuntimeLease,
                        cleanupFailed
                            ? "Background synchronization restoration failed and cleanup did not complete cleanly."
                            : "Background synchronization could not be restored.",
                        AndroidServiceStartPhase.RuntimeStartupFailed);
                }
            }

            return _backgroundState;
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    public async Task<AndroidServiceFrontendBackendClient> AttachInteractiveClientAsync(
        CancellationToken cancellationToken = default)
    {
        await _transitionLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnavailable();
            if (!_secureStorageAvailability.IsAvailable)
                throw new InvalidOperationException(
                    "The Android runtime is unavailable until the device is unlocked.");

            await LoadSettingsLockedAsync(cancellationToken);

            var reservedGeneration = ReserveNextAttachmentGenerationLocked();
            var previousClient = _interactiveClient;
            _interactiveClient = null;
            if (previousClient is not null)
            {
                previousClient.BeginClosingFromHost();
                var cleanup = await previousClient.CloseFromHostAsync();
                previousClient.CompleteDisposalFromHost();
                if (!cleanup.IsRuntimeSafe)
                {
                    await FailClosedUnsafeRuntimeLockedAsync(cleanup.Failure);
                    throw CreateUnsafeRuntimeException(cleanup.Failure);
                }
            }

            if (_backgroundState.IsEnabled && _backgroundLease is null)
            {
                try
                {
                    await EnsureBackgroundRuntimeLockedAsync(cancellationToken);
                }
                catch when (!cancellationToken.IsCancellationRequested)
                {
                    var startupFailureState = _backgroundState;
                    _foregroundServiceController.ExitForeground();
                    if (startupFailureState.FailureKind == AndroidBackgroundSyncFailureKind.ForegroundService)
                    {
                        _notificationAvailability = startupFailureState.NotificationAvailability;
                        _backgroundState = startupFailureState with
                        {
                            IsEnabled = true,
                            IsBackgroundLeaseActive = false,
                            IsForegroundActive = false,
                            IsRuntimeReady = false
                        };
                    }
                    else
                    {
                        _backgroundState = CreateFailureState(
                            true,
                            AndroidBackgroundSyncFailureKind.RuntimeLease,
                            "Background synchronization could not be restored.");
                    }
                }
            }

            var composition = EnsureCompositionLocked();
            var client = new AndroidServiceFrontendBackendClient(
                this,
                composition.Runtime,
                composition.LifetimeCoordinator,
                reservedGeneration);
            _interactiveClient = client;

            AndroidInteractiveOpenResult openResult;
            try
            {
                openResult = await client.OpenFromHostAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _interactiveClient = null;
                ReserveNextAttachmentGenerationLocked();
                client.BeginClosingFromHost();
                client.CompleteDisposalFromHost();
                await DisposeCompositionWhenUnownedLockedAsync();
                throw;
            }

            if (!openResult.IsRuntimeSafe)
            {
                _interactiveClient = null;
                ReserveNextAttachmentGenerationLocked();
                client.BeginClosingFromHost();
                client.CompleteDisposalFromHost();
                await FailClosedUnsafeRuntimeLockedAsync(openResult.Failure);
                throw CreateUnsafeRuntimeException(openResult.Failure);
            }

            if (!openResult.IsConnected)
            {
                if (IsDatabaseCompatibilityFailure(openResult.Failure))
                {
                    client.ActivateDatabaseRecoveryFromHost(openResult.Failure!);
                    return client;
                }

                _interactiveClient = null;
                ReserveNextAttachmentGenerationLocked();
                client.BeginClosingFromHost();
                client.CompleteDisposalFromHost();
                await DisposeCompositionWhenUnownedLockedAsync();
                throw openResult.Failure
                    ?? new InvalidOperationException("The Android interactive attachment could not be opened.");
            }

            client.ActivateFromHost();
            return client;
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    public async Task EnsureInteractiveConnectionAsync(
        AndroidServiceFrontendBackendClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        await _transitionLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnavailable();
            EnsureCurrentInteractiveClient(client);
            var result = await client.OpenFromHostAsync(cancellationToken);
            if (!result.IsRuntimeSafe)
            {
                _interactiveClient = null;
                ReserveNextAttachmentGenerationLocked();
                client.BeginClosingFromHost();
                client.CompleteDisposalFromHost();
                await FailClosedUnsafeRuntimeLockedAsync(result.Failure);
                throw CreateUnsafeRuntimeException(result.Failure);
            }

            if (result.Failure is not null)
                throw result.Failure;
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    public async Task DetachInteractiveClientAsync(AndroidServiceFrontendBackendClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        await _transitionLock.WaitAsync(CancellationToken.None);
        Exception? failure = null;
        try
        {
            if (!IsCurrentInteractiveClient(client))
            {
                client.CompleteDisposalFromHost();
                return;
            }

            _interactiveClient = null;
            ReserveNextAttachmentGenerationLocked();
            client.BeginClosingFromHost();
            var cleanup = await client.CloseFromHostAsync();
            client.CompleteDisposalFromHost();

            if (!cleanup.IsRuntimeSafe)
            {
                await FailClosedUnsafeRuntimeLockedAsync(cleanup.Failure);
                failure = cleanup.Failure ?? CreateUnsafeRuntimeException(null);
            }
            else
            {
                try
                {
                    await DisposeCompositionWhenUnownedLockedAsync();
                    if (_composition is null && _backgroundLease is null)
                        _foregroundServiceController.RequestStop();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }
        }
        finally
        {
            client.CompleteDisposalFromHost();
            _transitionLock.Release();
        }

        if (failure is not null)
            throw failure;
    }

    internal async Task<AndroidBackgroundSyncServiceState> GetBackgroundStateAsync(
        CancellationToken cancellationToken = default)
    {
        await _transitionLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (!_secureStorageAvailability.IsAvailable)
                return _backgroundState;

            await LoadSettingsLockedAsync(cancellationToken);
            return _backgroundState;
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    public async Task<AndroidBackgroundSyncServiceState> GetBackgroundStateFromAttachmentAsync(
        AndroidServiceFrontendBackendClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        await _transitionLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_runtimeUnsafe)
                return _backgroundState;
            if (!IsCurrentActiveAttachment(client))
                return CreateAttachmentAuthorityRejectedState();
            if (!_secureStorageAvailability.IsAvailable)
                return _backgroundState;

            await LoadSettingsLockedAsync(cancellationToken);
            return _backgroundState;
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    internal Task<AndroidBackgroundSyncServiceState> SetBackgroundEnabledFromServiceAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default) =>
        SetBackgroundEnabledCoreAsync(null, isEnabled, cancellationToken);

    public Task<AndroidBackgroundSyncServiceState> SetBackgroundEnabledFromAttachmentAsync(
        AndroidServiceFrontendBackendClient client,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return SetBackgroundEnabledCoreAsync(client, isEnabled, cancellationToken);
    }

    private async Task<AndroidBackgroundSyncServiceState> SetBackgroundEnabledCoreAsync(
        AndroidServiceFrontendBackendClient? client,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        await _transitionLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_runtimeUnsafe)
                return _backgroundState;
            if (client is not null && !IsCurrentActiveAttachment(client))
                return CreateAttachmentAuthorityRejectedState();
            if (!_secureStorageAvailability.IsAvailable)
                return CreateSecureStorageUnavailableState();

            await LoadSettingsLockedAsync(cancellationToken);
            _backgroundState = _backgroundState with { IsTransitionInProgress = true };

            return isEnabled
                ? await EnableBackgroundLockedAsync(cancellationToken)
                : await DisableBackgroundLockedAsync(cancellationToken);
        }
        finally
        {
            _backgroundState = _backgroundState with { IsTransitionInProgress = false };
            _transitionLock.Release();
        }
    }

    public async Task ResetDatabaseAsync(
        AndroidServiceFrontendBackendClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        await _transitionLock.WaitAsync(cancellationToken);
        AndroidInteractiveAttachmentState? stateBeforeReset = null;
        var replacementConnectionAdopted = false;
        try
        {
            ThrowIfDisposed();
            EnsureCurrentInteractiveClient(client);
            stateBeforeReset = client.SuspendAuthorityForResetFromHost();
            var composition = _composition
                ?? throw new InvalidOperationException("The Android runtime composition is unavailable.");
            var restoreBackground = _backgroundState.IsEnabled;

            var cleanup = await client.CloseFromHostAsync();
            if (!cleanup.IsRuntimeSafe)
            {
                await FailClosedUnsafeRuntimeLockedAsync(cleanup.Failure);
                throw CreateUnsafeRuntimeException(cleanup.Failure);
            }

            await ReleaseBackgroundLeaseLockedAsync();

            IBackendRuntimeLease? interactiveLease = null;
            IInteractiveBackendSession? interactiveSession = null;
            try
            {
                interactiveLease = await composition.LifetimeCoordinator.ResetDatabaseAndAcquireAsync(
                    BackendLifetimeReason.InteractiveUi,
                    cancellationToken);
                interactiveSession = await composition.Runtime.OpenInteractiveSessionAsync(cancellationToken);
                await client.AdoptResetConnectionFromHostAsync(interactiveLease, interactiveSession);
                interactiveLease = null;
                interactiveSession = null;
                replacementConnectionAdopted = true;

                if (restoreBackground)
                    await EnsureBackgroundRuntimeLockedAsync(cancellationToken);
            }
            catch
            {
                if (interactiveSession is not null)
                    await interactiveSession.DisposeAsync();
                if (interactiveLease is not null)
                    await interactiveLease.DisposeAsync();

                if (restoreBackground && _backgroundLease is null)
                {
                    var restorationFailureState = _backgroundState;
                    _foregroundServiceController.ExitForeground();
                    if (restorationFailureState.FailureKind ==
                        AndroidBackgroundSyncFailureKind.ForegroundService)
                    {
                        _notificationAvailability = restorationFailureState.NotificationAvailability;
                        _backgroundState = restorationFailureState with
                        {
                            IsEnabled = true,
                            IsBackgroundLeaseActive = false,
                            IsForegroundActive = false,
                            IsRuntimeReady = false
                        };
                    }
                    else
                    {
                        _backgroundState = CreateFailureState(
                            true,
                            AndroidBackgroundSyncFailureKind.RuntimeLease,
                            "Background synchronization could not be restored after database reset.");
                    }
                }

                throw;
            }
        }
        finally
        {
            if (stateBeforeReset.HasValue &&
                !_runtimeUnsafe &&
                IsCurrentInteractiveClient(client))
            {
                client.ResumeAuthorityAfterResetFromHost(
                    stateBeforeReset.Value,
                    replacementConnectionAdopted);
            }
            _transitionLock.Release();
        }
    }

    public ValueTask DisposeAsync() =>
        DisposeCoreAsync(requestForegroundServiceShutdown: true);

    internal ValueTask DisposeRuntimeResourcesAsync() =>
        DisposeCoreAsync(requestForegroundServiceShutdown: false);

    private async ValueTask DisposeCoreAsync(bool requestForegroundServiceShutdown)
    {
        await _transitionLock.WaitAsync(CancellationToken.None);
        Exception? failure = null;
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            ReserveNextAttachmentGenerationLocked();
            var client = _interactiveClient;
            _interactiveClient = null;
            if (client is not null)
            {
                client.BeginClosingFromHost();
                var cleanup = await client.CloseFromHostAsync();
                client.CompleteDisposalFromHost();
                if (!cleanup.IsRuntimeSafe)
                {
                    await FailClosedUnsafeRuntimeLockedAsync(cleanup.Failure);
                    failure = cleanup.Failure;
                }
            }

            failure = await CaptureFailureAsync(ReleaseBackgroundLeaseLockedAsync, failure);
            failure = await CaptureFailureAsync(DisposeCompositionLockedAsync, failure);

            if (requestForegroundServiceShutdown)
            {
                try
                {
                    _foregroundServiceController.ExitForeground();
                    _foregroundServiceController.RequestStop();
                }
                catch (Exception exception)
                {
                    failure = CombineFailures(failure, exception);
                }
            }

            _backgroundState = _backgroundState with
            {
                IsBackgroundLeaseActive = false,
                IsForegroundActive = false,
                IsDegraded = _runtimeUnsafe || failure is not null,
                FailureKind = _runtimeUnsafe
                    ? AndroidBackgroundSyncFailureKind.RuntimeUnsafe
                    : failure is null
                        ? AndroidBackgroundSyncFailureKind.None
                        : AndroidBackgroundSyncFailureKind.Shutdown,
                SafeMessage = _runtimeUnsafe
                    ? "Background synchronization stopped because the Android runtime could not be cleaned up safely."
                    : failure is null
                        ? null
                        : "Android runtime shutdown did not complete cleanly.",
                RequiresProcessRestart = _runtimeUnsafe,
                ServiceStartPhase = _runtimeUnsafe
                    ? AndroidServiceStartPhase.RuntimeUnsafe
                    : AndroidServiceStartPhase.Stopped,
                IsRuntimeReady = false,
                RequiresUserAction = _runtimeUnsafe,
                NotificationAvailability = AndroidNotificationAvailability.Unknown
            };
        }
        finally
        {
            _transitionLock.Release();
            GC.SuppressFinalize(this);
        }

        if (failure is not null)
            throw failure;
    }

    private async Task<AndroidBackgroundSyncServiceState> EnableBackgroundLockedAsync(
        CancellationToken cancellationToken)
    {
        if (_backgroundState.IsEnabled && _backgroundLease is not null)
        {
            _backgroundState = CreateOperationalState(true);
            return _backgroundState;
        }

        try
        {
            await _settingsStore.WriteAsync(new BackgroundSyncSettings(true), cancellationToken);
            _backgroundState = _backgroundState with { IsEnabled = true };
        }
        catch
        {
            var authoritativeEnabled = await TryReadAuthoritativeEnabledLockedAsync();
            if (authoritativeEnabled == true)
            {
                _backgroundState = _backgroundState with { IsEnabled = true };
            }
            else
            {
                _backgroundState = CreateFailureState(
                    authoritativeEnabled ?? _backgroundState.IsEnabled,
                    authoritativeEnabled.HasValue
                        ? AndroidBackgroundSyncFailureKind.SettingPersistence
                        : AndroidBackgroundSyncFailureKind.Rollback,
                    authoritativeEnabled.HasValue
                        ? "Background synchronization could not be enabled."
                        : "Background synchronization state is uncertain.");
                return _backgroundState;
            }
        }

        try
        {
            await EnsureBackgroundRuntimeLockedAsync(cancellationToken);
            return _backgroundState;
        }
        catch
        {
            var startupFailureState = _backgroundState;
            var cleanupFailed = false;
            try
            {
                await DisableBackgroundRuntimeLockedAsync(requestServiceStop: _interactiveClient is null);
            }
            catch
            {
                cleanupFailed = true;
            }

            if (startupFailureState.FailureKind == AndroidBackgroundSyncFailureKind.ForegroundService &&
                !cleanupFailed)
            {
                _notificationAvailability = startupFailureState.NotificationAvailability;
                _backgroundState = startupFailureState with
                {
                    IsEnabled = true,
                    IsBackgroundLeaseActive = false,
                    IsForegroundActive = false,
                    IsRuntimeReady = false
                };
            }
            else
            {
                _backgroundState = CreateFailureState(
                    true,
                    cleanupFailed
                        ? AndroidBackgroundSyncFailureKind.Shutdown
                        : AndroidBackgroundSyncFailureKind.RuntimeLease,
                    cleanupFailed
                        ? "Background synchronization could not be started and cleanup did not complete cleanly."
                        : "Background synchronization could not be started.",
                    AndroidServiceStartPhase.RuntimeStartupFailed);
            }

            return _backgroundState;
        }
    }

    private async Task<AndroidBackgroundSyncServiceState> DisableBackgroundLockedAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await _settingsStore.WriteAsync(new BackgroundSyncSettings(false), cancellationToken);
            _backgroundState = _backgroundState with { IsEnabled = false };
        }
        catch
        {
            var authoritativeEnabled = await TryReadAuthoritativeEnabledLockedAsync();
            if (authoritativeEnabled == false)
            {
                _backgroundState = _backgroundState with { IsEnabled = false };
            }
            else
            {
                _backgroundState = CreateFailureState(
                    authoritativeEnabled ?? _backgroundState.IsEnabled,
                    authoritativeEnabled.HasValue
                        ? AndroidBackgroundSyncFailureKind.SettingPersistence
                        : AndroidBackgroundSyncFailureKind.Rollback,
                    authoritativeEnabled.HasValue
                        ? "Background synchronization could not be disabled."
                        : "Background synchronization state is uncertain.");
                return _backgroundState;
            }
        }

        try
        {
            await DisableBackgroundRuntimeLockedAsync(requestServiceStop: true);
            _backgroundState = CreateOperationalState(false);
        }
        catch
        {
            _backgroundState = CreateFailureState(
                false,
                AndroidBackgroundSyncFailureKind.Shutdown,
                "Background synchronization is disabled, but cleanup did not complete cleanly.");
        }

        return _backgroundState;
    }

    private async Task EnsureBackgroundRuntimeLockedAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        if (!_secureStorageAvailability.IsAvailable)
        {
            await EnterDeferredUntilUnlockLockedAsync();
            return;
        }

        if (_backgroundLease is not null && _backgroundState.IsForegroundActive)
        {
            _backgroundState = CreateOperationalState(true);
            return;
        }

        _backgroundState = _backgroundState with
        {
            ServiceStartPhase = AndroidServiceStartPhase.ServiceRequested,
            IsRuntimeReady = false,
            RequiresUserAction = false
        };
        _foregroundServiceController.EnsureServiceStarted();
        var foregroundResult = _foregroundServiceController.EnterForeground(
            AndroidForegroundNotificationState.BackgroundSynchronizationActive);
        _notificationAvailability = foregroundResult.NotificationAvailability;
        if (!foregroundResult.IsForegroundEntered)
        {
            _backgroundState = CreateFailureState(
                true,
                AndroidBackgroundSyncFailureKind.ForegroundService,
                foregroundResult.SafeMessage ?? "Android rejected the foreground-service start.",
                foregroundResult.ServiceStartPhase,
                foregroundResult.RequiresUserAction);
            throw new InvalidOperationException(
                "The Android foreground service could not be entered.");
        }

        _backgroundState = _backgroundState with
        {
            IsForegroundActive = true,
            ServiceStartPhase = foregroundResult.ServiceStartPhase,
            RequiresUserAction = foregroundResult.RequiresUserAction,
            NotificationAvailability = foregroundResult.NotificationAvailability
        };

        var composition = EnsureCompositionLocked();
        if (_backgroundLease is null)
        {
            _backgroundLease = await composition.LifetimeCoordinator.AcquireAsync(
                BackendLifetimeReason.BackgroundSync,
                cancellationToken);
        }

        _backgroundState = CreateOperationalState(true);
    }

    private Task EnterDeferredUntilUnlockLockedAsync()
    {
        _foregroundServiceController.EnsureServiceStarted();
        var foregroundResult = _foregroundServiceController.EnterForeground(
            AndroidForegroundNotificationState.WaitingForDeviceUnlock);
        _notificationAvailability = foregroundResult.NotificationAvailability;
        if (!foregroundResult.IsForegroundEntered)
        {
            _backgroundState = CreateFailureState(
                isEnabled: true,
                AndroidBackgroundSyncFailureKind.ForegroundService,
                foregroundResult.SafeMessage ?? "Android could not enter foreground mode while waiting for device unlock.",
                foregroundResult.ServiceStartPhase,
                foregroundResult.RequiresUserAction);
            throw new InvalidOperationException(
                "The Android foreground service could not wait for device unlock.");
        }

        _backgroundState = new AndroidBackgroundSyncServiceState(
            IsEnabled: true,
            IsAvailable: true,
            IsDegraded: foregroundResult.RequiresUserAction,
            IsTransitionInProgress: false,
            FailureKind: AndroidBackgroundSyncFailureKind.SecureStorageDeferred,
            SafeMessage: foregroundResult.RequiresUserAction
                ? "Background synchronization is waiting for device unlock, but its notification is unavailable."
                : "Background synchronization is waiting for device unlock.",
            IsBackgroundLeaseActive: false,
            IsForegroundActive: true,
            IsSecureStorageDeferred: true,
            RequiresProcessRestart: false,
            ServiceStartPhase: AndroidServiceStartPhase.DeferredUntilUnlock,
            IsRuntimeReady: false,
            RequiresUserAction: foregroundResult.RequiresUserAction,
            NotificationAvailability: foregroundResult.NotificationAvailability);
        return Task.CompletedTask;
    }

    private async Task DisableBackgroundRuntimeLockedAsync(bool requestServiceStop)
    {
        Exception? failure = null;
        try
        {
            await ReleaseBackgroundLeaseLockedAsync();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            _foregroundServiceController.ExitForeground();
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException(failure, exception);
        }

        _notificationAvailability = AndroidNotificationAvailability.Unknown;
        _backgroundState = _backgroundState with
        {
            IsBackgroundLeaseActive = false,
            IsForegroundActive = false,
            IsSecureStorageDeferred = false,
            ServiceStartPhase = AndroidServiceStartPhase.Stopped,
            IsRuntimeReady = false,
            RequiresUserAction = false,
            NotificationAvailability = AndroidNotificationAvailability.Unknown
        };

        try
        {
            await DisposeCompositionWhenUnownedLockedAsync();
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException(failure, exception);
        }

        if (requestServiceStop)
        {
            try
            {
                _foregroundServiceController.RequestStop();
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

    private async Task AcquireBackgroundLeaseLockedAsync(CancellationToken cancellationToken)
    {
        if (_backgroundLease is not null)
            return;

        var composition = EnsureCompositionLocked();
        _backgroundLease = await composition.LifetimeCoordinator.AcquireAsync(
            BackendLifetimeReason.BackgroundSync,
            cancellationToken);
        _backgroundState = _backgroundState with { IsBackgroundLeaseActive = true };
    }

    private async Task ReleaseBackgroundLeaseLockedAsync()
    {
        var lease = _backgroundLease;
        _backgroundLease = null;
        _backgroundState = _backgroundState with { IsBackgroundLeaseActive = false };
        if (lease is not null)
            await lease.DisposeAsync();
    }

    private BackendRuntimeComposition EnsureCompositionLocked()
    {
        ThrowIfUnavailable();
        if (_composition is not null)
            return _composition;

        _composition = _compositionFactory.Create()
            ?? throw new InvalidOperationException("The Android runtime composition factory returned no composition.");
        return _composition;
    }

    private async Task DisposeCompositionWhenUnownedLockedAsync()
    {
        if (_interactiveClient is not null || _backgroundLease is not null)
            return;

        await DisposeCompositionLockedAsync();
    }

    private async Task DisposeCompositionLockedAsync()
    {
        var composition = _composition;
        _composition = null;
        if (composition is not null)
            await composition.Runtime.DisposeAsync();
    }

    private async Task<bool?> TryReadAuthoritativeEnabledLockedAsync()
    {
        try
        {
            var settings = await _settingsStore.ReadAsync(CancellationToken.None);
            return settings.IsEnabled;
        }
        catch
        {
            return null;
        }
    }

    private async Task LoadSettingsLockedAsync(CancellationToken cancellationToken)
    {
        if (_settingsLoaded)
            return;

        try
        {
            var settings = await _settingsStore.ReadAsync(cancellationToken);
            _settingsLoaded = true;
            _backgroundState = CreateOperationalState(settings.IsEnabled);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _settingsLoaded = false;
            _backgroundState = CreateFailureState(
                false,
                AndroidBackgroundSyncFailureKind.SettingRead,
                "Background synchronization settings are unavailable.");
        }
    }

    internal void EnsureEndpointAuthority(AndroidServiceFrontendBackendClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!IsCurrentActiveAttachment(client))
            throw new InvalidOperationException("The Android interactive service attachment is no longer authoritative.");
    }

    private void EnsureCurrentInteractiveClient(AndroidServiceFrontendBackendClient client)
    {
        if (_runtimeUnsafe ||
            !IsCurrentInteractiveClient(client) ||
            !client.HasConnectionAuthority)
        {
            throw new InvalidOperationException("The Android interactive service attachment is no longer active.");
        }
    }

    private bool IsCurrentInteractiveClient(AndroidServiceFrontendBackendClient client) =>
        ReferenceEquals(Volatile.Read(ref _interactiveClient), client) &&
        client.AttachmentGeneration == Interlocked.Read(ref _attachmentGeneration);

    private bool IsCurrentActiveAttachment(AndroidServiceFrontendBackendClient client) =>
        !_runtimeUnsafe &&
        IsCurrentInteractiveClient(client) &&
        client.HasMutationAuthority;

    private static bool IsDatabaseCompatibilityFailure(Exception? failure)
    {
        if (failure is null)
            return false;

        return IsDatabaseCompatibilityFailure(
            failure,
            new HashSet<Exception>(ReferenceEqualityComparer.Instance));
    }

    private static bool IsDatabaseCompatibilityFailure(
        Exception failure,
        ISet<Exception> visited)
    {
        if (!visited.Add(failure))
            return false;
        if (failure is DatabaseVersionNotSupportedException)
            return true;

        if (failure is AggregateException aggregateFailure)
        {
            return aggregateFailure.InnerExceptions.Any(
                innerFailure => IsDatabaseCompatibilityFailure(innerFailure, visited));
        }

        return failure.InnerException is not null &&
            IsDatabaseCompatibilityFailure(failure.InnerException, visited);
    }

    private long ReserveNextAttachmentGenerationLocked() => ++_attachmentGeneration;

    private async Task FailClosedUnsafeRuntimeLockedAsync(Exception? failure)
    {
        if (_runtimeUnsafe)
            return;

        _runtimeUnsafe = true;
        ReserveNextAttachmentGenerationLocked();
        var client = _interactiveClient;
        _interactiveClient = null;
        client?.BeginClosingFromHost();

        Exception? shutdownFailure = failure;
        shutdownFailure = await CaptureFailureAsync(ReleaseBackgroundLeaseLockedAsync, shutdownFailure);
        shutdownFailure = await CaptureFailureAsync(DisposeCompositionLockedAsync, shutdownFailure);
        client?.CompleteDisposalFromHost();

        try
        {
            _foregroundServiceController.ExitForeground();
        }
        catch (Exception exception)
        {
            shutdownFailure = CombineFailures(shutdownFailure, exception);
        }

        try
        {
            _foregroundServiceController.RequestStop();
        }
        catch (Exception exception)
        {
            shutdownFailure = CombineFailures(shutdownFailure, exception);
        }

        _backgroundState = _backgroundState with
        {
            IsAvailable = false,
            IsDegraded = true,
            IsTransitionInProgress = false,
            FailureKind = AndroidBackgroundSyncFailureKind.RuntimeUnsafe,
            SafeMessage = "Background synchronization stopped because the Android runtime could not be cleaned up safely.",
            IsBackgroundLeaseActive = false,
            IsForegroundActive = false,
            IsSecureStorageDeferred = false,
            RequiresProcessRestart = true,
            ServiceStartPhase = AndroidServiceStartPhase.RuntimeUnsafe,
            IsRuntimeReady = false,
            RequiresUserAction = true,
            NotificationAvailability = AndroidNotificationAvailability.Unknown
        };

        try
        {
            _foregroundServiceController.RequestProcessTermination();
        }
        catch
        {
        }
    }

    private AndroidBackgroundSyncServiceState CreateSecureStorageUnavailableState() =>
        _backgroundState with
        {
            IsAvailable = false,
            IsDegraded = true,
            IsTransitionInProgress = false,
            FailureKind = AndroidBackgroundSyncFailureKind.SecureStorageDeferred,
            SafeMessage = "Background synchronization settings are unavailable until the device is unlocked.",
            IsSecureStorageDeferred = true,
            ServiceStartPhase = AndroidServiceStartPhase.DeferredUntilUnlock,
            IsRuntimeReady = false,
            RequiresUserAction = false
        };

    private AndroidBackgroundSyncServiceState CreateAttachmentAuthorityRejectedState() =>
        _backgroundState with
        {
            IsAvailable = false,
            IsDegraded = true,
            IsTransitionInProgress = false,
            FailureKind = AndroidBackgroundSyncFailureKind.AttachmentAuthority,
            SafeMessage = "This activity is no longer connected to the Android runtime service.",
            RequiresUserAction = false
        };

    private InvalidOperationException CreateUnsafeRuntimeException(Exception? failure) =>
        new(
            "The Android runtime cannot continue safely in this process.",
            failure);

    private async Task<Exception?> CaptureFailureAsync(
        Func<Task> operation,
        Exception? existingFailure)
    {
        try
        {
            await operation();
            return existingFailure;
        }
        catch (Exception exception)
        {
            return CombineFailures(existingFailure, exception);
        }
    }

    private Exception? CombineFailures(Exception? first, Exception? second)
    {
        if (first is null)
            return second;
        if (second is null)
            return first;
        return new AggregateException(first, second);
    }

    private void ThrowIfUnavailable()
    {
        ThrowIfDisposed();
        if (_runtimeUnsafe)
            throw CreateUnsafeRuntimeException(null);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AndroidRuntimeServiceHost));
    }

    private AndroidBackgroundSyncServiceState CreateOperationalState(bool isEnabled)
    {
        var hasActiveBackgroundRuntime = isEnabled && _backgroundLease is not null;
        var notificationsAvailable =
            _notificationAvailability == AndroidNotificationAvailability.Available;
        return new AndroidBackgroundSyncServiceState(
            isEnabled,
            IsAvailable: true,
            IsDegraded: hasActiveBackgroundRuntime && !notificationsAvailable,
            IsTransitionInProgress: false,
            hasActiveBackgroundRuntime && !notificationsAvailable
                ? AndroidBackgroundSyncFailureKind.ForegroundService
                : AndroidBackgroundSyncFailureKind.None,
            hasActiveBackgroundRuntime && !notificationsAvailable
                ? "Background synchronization is running, but its notification is unavailable."
                : null,
            IsBackgroundLeaseActive: hasActiveBackgroundRuntime,
            IsForegroundActive: hasActiveBackgroundRuntime,
            IsSecureStorageDeferred: false,
            RequiresProcessRestart: false,
            hasActiveBackgroundRuntime
                ? notificationsAvailable
                    ? AndroidServiceStartPhase.RuntimeReady
                    : AndroidServiceStartPhase.NotificationUnavailable
                : AndroidServiceStartPhase.Stopped,
            IsRuntimeReady: hasActiveBackgroundRuntime,
            RequiresUserAction: hasActiveBackgroundRuntime && !notificationsAvailable,
            _notificationAvailability);
    }

    private AndroidBackgroundSyncServiceState CreateFailureState(
        bool isEnabled,
        AndroidBackgroundSyncFailureKind failureKind,
        string safeMessage,
        AndroidServiceStartPhase serviceStartPhase = AndroidServiceStartPhase.RuntimeStartupFailed,
        bool requiresUserAction = false) => new(
            isEnabled,
            IsAvailable: true,
            IsDegraded: true,
            IsTransitionInProgress: false,
            failureKind,
            safeMessage,
            IsBackgroundLeaseActive: false,
            IsForegroundActive: false,
            IsSecureStorageDeferred: false,
            RequiresProcessRestart: false,
            serviceStartPhase,
            IsRuntimeReady: false,
            requiresUserAction,
            _notificationAvailability);

    private AndroidBackgroundSyncServiceState CreateInitialState() => new(
        IsEnabled: false,
        IsAvailable: true,
        IsDegraded: false,
        IsTransitionInProgress: false,
        AndroidBackgroundSyncFailureKind.None,
        SafeMessage: null,
        IsBackgroundLeaseActive: false,
        IsForegroundActive: false,
        IsSecureStorageDeferred: false,
        RequiresProcessRestart: false,
        AndroidServiceStartPhase.Stopped,
        IsRuntimeReady: false,
        RequiresUserAction: false,
        AndroidNotificationAvailability.Unknown);
}
