namespace PasswordManagerLocal.Common.Contracts.Runtime;

public enum BackendRuntimeFailureKind
{
    None,
    DatabaseCompatibility,
    PlatformKeyUnavailable,
    StorageUnavailable,
    StartupFailure,
    InteractiveCleanupFailure,
    ShutdownFailure
}
