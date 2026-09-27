namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public enum WindowsBackgroundSyncFailureKind
{
    None = 0,
    SettingRead = 1,
    SettingPersistence = 2,
    StartupRegistration = 3,
    RuntimeLease = 4,
    Rollback = 5
}
