namespace PasswordManagerLocal.Windows.Ipc.Contracts.Status;

public static class BackendRuntimeFailureStatusKindExtensions
{
    public static bool CanRetryStartup(this BackendRuntimeFailureStatusKind kind) =>
        kind.RequiresDatabaseReset() || kind is
            BackendRuntimeFailureStatusKind.PlatformKeyUnavailable or
            BackendRuntimeFailureStatusKind.StorageUnavailable or
            BackendRuntimeFailureStatusKind.StartupFailure;

    public static bool RequiresDatabaseReset(this BackendRuntimeFailureStatusKind kind) => kind is
        BackendRuntimeFailureStatusKind.DatabaseCompatibility or
        BackendRuntimeFailureStatusKind.DatabaseConfigurationInvalid or
        BackendRuntimeFailureStatusKind.DatabaseKeyUnavailable or
        BackendRuntimeFailureStatusKind.DatabaseUnreadable or
        BackendRuntimeFailureStatusKind.DatabaseResetIncomplete;
}
