namespace PasswordManagerLocal.Android.Runtime;

public enum AndroidForegroundEntryFailureKind
{
    None,
    NotificationManagerUnavailable,
    NotificationChannelUnavailable,
    ForegroundStartRejected,
    NotificationPostingFailed
}
