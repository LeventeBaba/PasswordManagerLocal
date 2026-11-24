namespace PasswordManagerLocal.Common.Contracts.Runtime;

public static class BackendRuntimeFailureKindExtensions
{
    public static bool CanRetryStartup(this BackendRuntimeFailureKind kind) =>
        kind.RequiresDatabaseReset() || kind is
            BackendRuntimeFailureKind.PlatformKeyUnavailable or
            BackendRuntimeFailureKind.StorageUnavailable or
            BackendRuntimeFailureKind.StartupFailure;

    public static bool RequiresDatabaseReset(this BackendRuntimeFailureKind kind) => kind is
        BackendRuntimeFailureKind.DatabaseCompatibility or
        BackendRuntimeFailureKind.DatabaseConfigurationInvalid or
        BackendRuntimeFailureKind.DatabaseKeyUnavailable or
        BackendRuntimeFailureKind.DatabaseUnreadable or
        BackendRuntimeFailureKind.DatabaseResetIncomplete;
}
