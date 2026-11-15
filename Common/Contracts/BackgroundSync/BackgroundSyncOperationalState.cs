namespace PasswordManagerLocal.Common.Contracts.BackgroundSync;

public enum BackgroundSyncOperationalState
{
    Unspecified = 0,
    Disabled,
    EnabledIdle,
    Starting,
    Running,
    DegradedRetrying,
    RestartRequired,
    Unavailable
}
