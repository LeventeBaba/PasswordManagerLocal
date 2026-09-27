namespace PasswordManagerLocal.Android.Runtime;

public enum AndroidServiceStartPhase
{
    Stopped,
    ServiceRequested,
    ServiceStarting,
    ForegroundEntered,
    RuntimeReady,
    DeferredUntilUnlock,
    NotificationUnavailable,
    ForegroundStartRejected,
    RuntimeStartupFailed,
    RuntimeUnsafe
}
