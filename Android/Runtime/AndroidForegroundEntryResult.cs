namespace PasswordManagerLocal.Android.Runtime;

public sealed record AndroidForegroundEntryResult(
    bool IsForegroundEntered,
    AndroidNotificationAvailability NotificationAvailability,
    AndroidForegroundEntryFailureKind FailureKind,
    bool RequiresUserAction,
    string? SafeMessage)
{
    public AndroidServiceStartPhase ServiceStartPhase => IsForegroundEntered
        ? NotificationAvailability == AndroidNotificationAvailability.Available
            ? AndroidServiceStartPhase.ForegroundEntered
            : AndroidServiceStartPhase.NotificationUnavailable
        : FailureKind == AndroidForegroundEntryFailureKind.ForegroundStartRejected
            ? AndroidServiceStartPhase.ForegroundStartRejected
            : AndroidServiceStartPhase.NotificationUnavailable;
}
