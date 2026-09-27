namespace PasswordManagerLocal.Android.Runtime;

public enum AndroidBackgroundSyncFailureKind
{
    None = 0,
    SettingRead = 1,
    SettingPersistence = 2,
    ForegroundService = 3,
    RuntimeComposition = 4,
    RuntimeLease = 5,
    SecureStorageDeferred = 6,
    Rollback = 7,
    Shutdown = 8,
    AttachmentAuthority = 9,
    RuntimeUnsafe = 10
}
