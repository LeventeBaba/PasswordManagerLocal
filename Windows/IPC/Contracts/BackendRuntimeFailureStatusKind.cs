namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public enum BackendRuntimeFailureStatusKind
{
    None = 0,
    DatabaseCompatibility = 1,
    PlatformKeyUnavailable = 2,
    StorageUnavailable = 3,
    StartupFailure = 4,
    InteractiveCleanupFailure = 5,
    ShutdownFailure = 6
}
