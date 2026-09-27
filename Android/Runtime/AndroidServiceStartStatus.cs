namespace PasswordManagerLocal.Android.Runtime;

public sealed record AndroidServiceStartStatus(
    AndroidServiceStartPhase Phase,
    bool IsForegroundEntered,
    bool IsRuntimeReady,
    AndroidNotificationAvailability NotificationAvailability,
    bool RequiresUserAction,
    bool RequiresProcessRestart,
    string? SafeMessage);
