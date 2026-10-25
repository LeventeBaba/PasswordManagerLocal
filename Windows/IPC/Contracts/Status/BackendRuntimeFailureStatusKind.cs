namespace PasswordManagerLocal.Windows.Ipc.Contracts.Status;

public enum BackendRuntimeFailureStatusKind
{
    None = 0,
    DatabaseCompatibility = 1,
    PlatformKeyUnavailable = 2,
    StorageUnavailable = 3,
    StartupFailure = 4,
    InteractiveCleanupFailure = 5,
    ShutdownFailure = 6,
    DatabaseConfigurationInvalid = 7,
    DatabaseKeyUnavailable = 8,
    DatabaseUnreadable = 9
}
