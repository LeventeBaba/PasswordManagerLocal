using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using PasswordManagerLocal.Android.Runtime;
using PasswordManagerLocal.Android.Backend;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Hosting;

namespace PasswordManagerLocal.Android.Frontend;

[Service(
    Name = "com.levibaba.passwordmanagerlocal.PasswordManagerBackgroundService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeConnectedDevice)]
public sealed class PasswordManagerBackgroundService : Service, IAndroidRuntimeCompositionFactory
{
    private readonly AndroidServiceStartStateMachine _serviceStartState = new();
    private readonly AndroidBackgroundRestorationPolicy _restorationPolicy = new();
    private readonly object _restorationGate = new();
    private AndroidRuntimeServiceHost? _runtimeHost;
    private Task? _restorationTask;
    private AndroidForegroundServiceController? _foregroundController;
    private AndroidSecureStorageAvailability? _secureStorageAvailability;
    private PasswordManagerBackgroundServiceBinder? _binder;
    private AndroidDeferredUnlockReceiver? _deferredUnlockReceiver;
    private int _isDestroying;

    public override void OnCreate()
    {
        base.OnCreate();
        try
        {
            var applicationContext = ApplicationContext
                ?? throw new InvalidOperationException("The Android application context is unavailable.");
            var filesDirectory = applicationContext.FilesDir?.AbsolutePath;
            if (string.IsNullOrWhiteSpace(filesDirectory))
                throw new InvalidOperationException("The Android application-data directory is unavailable.");

            var applicationDataDirectory = Path.Combine(
                filesDirectory,
                ApplicationFileNames.AppFolderName);
            _foregroundController = new AndroidForegroundServiceController(this);
            _secureStorageAvailability = new AndroidSecureStorageAvailability(applicationContext);
            _runtimeHost = new AndroidRuntimeServiceHost(
                this,
                new FileBackgroundSyncSettingsStore(applicationDataDirectory),
                _foregroundController,
                _secureStorageAvailability);
            _binder = new PasswordManagerBackgroundServiceBinder(this);
            RegisterDeferredUnlockReceiver();
        }
        catch
        {
            CleanupFailedCreation();
            throw;
        }
    }

    public override IBinder OnBind(Intent? intent) =>
        _binder ?? throw new InvalidOperationException("The Android runtime-service binder is unavailable.");

    public override StartCommandResult OnStartCommand(
        Intent? intent,
        StartCommandFlags flags,
        int startId)
    {
        var controller = _foregroundController;
        try
        {
            if (Volatile.Read(ref _isDestroying) != 0)
            {
                controller?.RequestStop();
                return StartCommandResult.NotSticky;
            }

            controller?.MarkServiceStarted();
            var runtimeSnapshot = GetRuntimeHost().Snapshot;
            if (runtimeSnapshot.IsRuntimeUnsafe)
            {
                _serviceStartState.RecordRuntimeUnsafe(
                    "The Android runtime requires a fresh application process.");
                controller?.ExitForeground();
                controller?.RequestStop();
                controller?.RequestProcessTermination();
                return StartCommandResult.NotSticky;
            }

            if (runtimeSnapshot.IsForeground)
            {
                QueueBackgroundRestoration();
                return StartCommandResult.Sticky;
            }

            _serviceStartState.RecordServiceRequested();
            _serviceStartState.RecordServiceStarting();
            var secureStorageAvailable = _secureStorageAvailability?.IsAvailable == true;
            var restorationDecision = _restorationPolicy.Decide(
                AndroidBackgroundRestorationTrigger.StickyServiceRestart,
                secureStorageAvailable,
                isBackgroundEnabled: null);
            var foregroundResult = controller?.EnterForeground(
                restorationDecision.Action == AndroidBackgroundRestorationAction.DeferUntilUnlock
                    ? AndroidForegroundNotificationState.WaitingForDeviceUnlock
                    : AndroidForegroundNotificationState.BackgroundSynchronizationStarting)
                ?? new AndroidForegroundEntryResult(
                    IsForegroundEntered: false,
                    AndroidNotificationAvailability.Unknown,
                    AndroidForegroundEntryFailureKind.NotificationManagerUnavailable,
                    RequiresUserAction: false,
                    "Android foreground-service control is unavailable.");
            _serviceStartState.RecordForegroundEntry(foregroundResult);
            if (!foregroundResult.IsForegroundEntered)
            {
                controller?.ExitForeground();
                controller?.RequestStop();
                return StartCommandResult.NotSticky;
            }

            QueueBackgroundRestoration();
            return StartCommandResult.Sticky;
        }
        catch
        {
            controller?.ExitForeground();
            controller?.RequestStop();
            _serviceStartState.RecordStopped();
            return StartCommandResult.NotSticky;
        }
    }

    BackendRuntimeComposition IAndroidRuntimeCompositionFactory.Create()
    {
        var applicationContext = ApplicationContext
            ?? throw new InvalidOperationException("The Android application context is unavailable.");
        return AndroidBackendRuntimeFactory.Create(applicationContext);
    }

    public AndroidRuntimeServiceSnapshot RuntimeSnapshot => GetRuntimeHost().Snapshot;
    public AndroidServiceStartStatus ServiceStartStatus => _serviceStartState.Status;

    public Task<AndroidServiceFrontendBackendClient> AttachInteractiveClientAsync(
        CancellationToken cancellationToken = default) =>
        GetRuntimeHost().AttachInteractiveClientAsync(cancellationToken);

    public Task<AndroidBackgroundSyncServiceState> GetBackgroundStateAsync(
        AndroidServiceFrontendBackendClient client,
        CancellationToken cancellationToken = default) =>
        GetRuntimeHost().GetBackgroundStateFromAttachmentAsync(client, cancellationToken);

    public Task<AndroidBackgroundSyncServiceState> SetBackgroundEnabledAsync(
        AndroidServiceFrontendBackendClient client,
        bool isEnabled,
        CancellationToken cancellationToken = default) =>
        GetRuntimeHost().SetBackgroundEnabledFromAttachmentAsync(client, isEnabled, cancellationToken);

    public override void OnDestroy()
    {
        Interlocked.Exchange(ref _isDestroying, 1);
        lock (_restorationGate)
            _restorationTask = null;

        UnregisterDeferredUnlockReceiver();
        var host = Interlocked.Exchange(ref _runtimeHost, null);
        try
        {
            _foregroundController?.ExitForeground();
        }
        catch
        {
        }

        _foregroundController = null;
        _secureStorageAvailability = null;
        _binder?.Dispose();
        _binder = null;
        _serviceStartState.RecordStopped();
        try
        {
            base.OnDestroy();
        }
        finally
        {
            if (host is not null)
                _ = Task.Run(() => DisposeRuntimeHostAfterServiceDestructionAsync(host));
        }
    }

    private void RegisterDeferredUnlockReceiver()
    {
        var receiver = new AndroidDeferredUnlockReceiver(QueueBackgroundRestoration);
        var filter = new IntentFilter(Intent.ActionUserUnlocked);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
        else
            RegisterReceiver(receiver, filter);

        _deferredUnlockReceiver = receiver;
    }

    private void UnregisterDeferredUnlockReceiver()
    {
        var receiver = Interlocked.Exchange(ref _deferredUnlockReceiver, null);
        if (receiver is null)
            return;

        try
        {
            UnregisterReceiver(receiver);
        }
        catch
        {
        }
    }

    private void QueueBackgroundRestoration()
    {
        lock (_restorationGate)
        {
            if (Volatile.Read(ref _isDestroying) != 0)
                return;
            if (_restorationTask is { IsCompleted: false })
                return;

            _restorationTask = RestoreBackgroundStateAsync();
        }
    }

    private async Task RestoreBackgroundStateAsync()
    {
        try
        {
            var state = await GetRuntimeHost().RestoreBackgroundStateAsync();
            if (Volatile.Read(ref _isDestroying) == 0)
                _serviceStartState.RecordRuntimeState(state);
        }
        catch
        {
            _foregroundController?.ExitForeground();
            _foregroundController?.RequestStop();
            if (Volatile.Read(ref _isDestroying) == 0)
                _serviceStartState.RecordStopped();
        }
    }

    private void CleanupFailedCreation()
    {
        Interlocked.Exchange(ref _isDestroying, 1);
        var host = Interlocked.Exchange(ref _runtimeHost, null);

        try
        {
            _foregroundController?.ExitForeground();
            _foregroundController?.RequestStop();
        }
        catch
        {
        }

        UnregisterDeferredUnlockReceiver();
        _foregroundController = null;
        _secureStorageAvailability = null;
        _binder?.Dispose();
        _binder = null;

        if (host is not null)
            _ = Task.Run(() => DisposeRuntimeHostAfterServiceDestructionAsync(host));
    }

    private static async Task DisposeRuntimeHostAfterServiceDestructionAsync(
        AndroidRuntimeServiceHost host)
    {
        try
        {
            await host.DisposeRuntimeResourcesAsync();
        }
        catch
        {
            // Android lifecycle callbacks cannot wait for asynchronous runtime cleanup.
            // Normal stop paths drain the runtime before requesting service destruction;
            // this is a best-effort fallback for abnormal lifecycle termination.
        }
    }

    private AndroidRuntimeServiceHost GetRuntimeHost() =>
        _runtimeHost ?? throw new InvalidOperationException("The Android runtime service is unavailable.");
}
