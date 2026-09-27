using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using PasswordManagerLocal.Android.Runtime;

namespace PasswordManagerLocal.Android.Frontend;

public sealed class AndroidForegroundServiceController : IAndroidForegroundServiceController
{
    public const string NotificationChannelId = "password_manager_background_sync";
    public const int NotificationId = 41010;

    private readonly PasswordManagerBackgroundService _service;
    private readonly object _gate = new();
    private bool _isForeground;
    private bool _isServiceStarted;
    private AndroidForegroundNotificationState? _foregroundNotificationState;

    public AndroidForegroundServiceController(PasswordManagerBackgroundService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public void EnsureServiceStarted()
    {
        lock (_gate)
        {
            if (_isServiceStarted)
                return;

            var intent = new Intent(_service, typeof(PasswordManagerBackgroundService));
            _service.StartForegroundService(intent);
            _isServiceStarted = true;
        }
    }

    public void MarkServiceStarted()
    {
        lock (_gate)
            _isServiceStarted = true;
    }

    public AndroidForegroundEntryResult EnterForeground(AndroidForegroundNotificationState state)
    {
        lock (_gate)
        {
            var manager = _service.GetSystemService(Context.NotificationService) as NotificationManager;
            if (manager is null)
            {
                return new AndroidForegroundEntryResult(
                    IsForegroundEntered: false,
                    AndroidNotificationAvailability.Unknown,
                    AndroidForegroundEntryFailureKind.NotificationManagerUnavailable,
                    RequiresUserAction: false,
                    "Android notification services are unavailable.");
            }

            NotificationChannel channel;
            try
            {
                channel = EnsureNotificationChannel(manager);
            }
            catch
            {
                return new AndroidForegroundEntryResult(
                    IsForegroundEntered: false,
                    AndroidNotificationAvailability.Unknown,
                    AndroidForegroundEntryFailureKind.NotificationChannelUnavailable,
                    RequiresUserAction: true,
                    "The background synchronization notification channel is unavailable.");
            }

            var notificationAvailability = GetNotificationAvailability(manager, channel);
            if (_isForeground &&
                (_foregroundNotificationState == state ||
                    _foregroundNotificationState == AndroidForegroundNotificationState.BackgroundSynchronizationActive))
            {
                return new AndroidForegroundEntryResult(
                    IsForegroundEntered: true,
                    NotificationAvailability: notificationAvailability,
                    FailureKind: AndroidForegroundEntryFailureKind.None,
                    RequiresUserAction: notificationAvailability != AndroidNotificationAvailability.Available,
                    SafeMessage: GetNotificationAvailabilityMessage(notificationAvailability));
            }

            Notification notification;
            try
            {
                notification = BuildNotification(state);
            }
            catch
            {
                return new AndroidForegroundEntryResult(
                    IsForegroundEntered: false,
                    NotificationAvailability: notificationAvailability,
                    FailureKind: AndroidForegroundEntryFailureKind.NotificationPostingFailed,
                    RequiresUserAction: true,
                    SafeMessage: "The background synchronization notification could not be created.");
            }

            var wasForeground = _isForeground;
            var previousNotificationState = _foregroundNotificationState;
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                {
                    _service.StartForeground(
                        NotificationId,
                        notification,
                        ForegroundService.TypeConnectedDevice);
                }
                else
                {
                    _service.StartForeground(NotificationId, notification);
                }

                _isForeground = true;
                _foregroundNotificationState = state;
                return new AndroidForegroundEntryResult(
                    IsForegroundEntered: true,
                    NotificationAvailability: notificationAvailability,
                    FailureKind: AndroidForegroundEntryFailureKind.None,
                    RequiresUserAction: notificationAvailability != AndroidNotificationAvailability.Available,
                    SafeMessage: GetNotificationAvailabilityMessage(notificationAvailability));
            }
            catch
            {
                _isForeground = wasForeground;
                _foregroundNotificationState = previousNotificationState;
                return new AndroidForegroundEntryResult(
                    IsForegroundEntered: false,
                    NotificationAvailability: notificationAvailability,
                    FailureKind: AndroidForegroundEntryFailureKind.ForegroundStartRejected,
                    RequiresUserAction: true,
                    SafeMessage: "Android rejected the background synchronization foreground service.");
            }
        }
    }

    public void ExitForeground()
    {
        lock (_gate)
        {
            if (!_isForeground)
                return;

            _service.StopForeground(StopForegroundFlags.Remove);
            _isForeground = false;
            _foregroundNotificationState = null;
        }
    }

    public void RequestStop()
    {
        lock (_gate)
        {
            if (!_isServiceStarted)
                return;

            _isServiceStarted = false;
            _service.StopSelf();
        }
    }

    public void RequestProcessTermination()
    {
        global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
    }

    private NotificationChannel EnsureNotificationChannel(NotificationManager manager)
    {
        var existing = manager.GetNotificationChannel(NotificationChannelId);
        if (existing is not null)
            return existing;

        var channel = new NotificationChannel(
            NotificationChannelId,
            "Background synchronization",
            NotificationImportance.Low)
        {
            Description = "Keeps already-enrolled password-manager devices synchronized."
        };
        channel.SetShowBadge(false);
        manager.CreateNotificationChannel(channel);
        return manager.GetNotificationChannel(NotificationChannelId)
            ?? throw new InvalidOperationException("The notification channel was not created.");
    }

    private AndroidNotificationAvailability GetNotificationAvailability(
        NotificationManager manager,
        NotificationChannel channel)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu &&
            _service.CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) !=
                Permission.Granted)
        {
            return AndroidNotificationAvailability.PermissionDenied;
        }

        if (!manager.AreNotificationsEnabled())
            return AndroidNotificationAvailability.ApplicationDisabled;

        if (channel.Importance == NotificationImportance.None)
            return AndroidNotificationAvailability.ChannelDisabled;

        return AndroidNotificationAvailability.Available;
    }

    private Notification BuildNotification(AndroidForegroundNotificationState state)
    {
        var launchIntent = new Intent(_service, typeof(MainActivity));
        launchIntent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop | ActivityFlags.NewTask);
        var pendingIntent = PendingIntent.GetActivity(
            _service,
            0,
            launchIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        return new Notification.Builder(_service, NotificationChannelId)
            .SetSmallIcon(Resource.Drawable.ic_stat_password_manager)
            .SetContentTitle("Password Manager")
            .SetContentText(state switch
            {
                AndroidForegroundNotificationState.WaitingForDeviceUnlock =>
                    "Background synchronization is waiting for device unlock",
                AndroidForegroundNotificationState.BackgroundSynchronizationStarting =>
                    "Preparing background synchronization",
                _ => "Background synchronization is active"
            })
            .SetContentIntent(pendingIntent)
            .SetCategory(Notification.CategoryService)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .Build();
    }

    private string? GetNotificationAvailabilityMessage(
        AndroidNotificationAvailability availability) => availability switch
    {
        AndroidNotificationAvailability.PermissionDenied =>
            "Background synchronization is running, but notification permission is denied.",
        AndroidNotificationAvailability.ApplicationDisabled =>
            "Background synchronization is running, but application notifications are disabled.",
        AndroidNotificationAvailability.ChannelDisabled =>
            "Background synchronization is running, but its notification channel is disabled.",
        _ => null
    };
}
