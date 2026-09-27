namespace PasswordManagerLocal.Common.Contracts.BackgroundSync;

public enum BackgroundSyncClientFailureKind
{
    None = 0,
    SettingPersistence = 1,
    StartupRegistration = 2,
    Runtime = 3,
    Rollback = 4,
    Unavailable = 5
}
