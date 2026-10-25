namespace PasswordManagerLocal.Windows.Ipc.Contracts.Status;

public static class BackendRuntimeFailureStatusKindExtensions
{
    public static bool RequiresDatabaseReset(this BackendRuntimeFailureStatusKind kind) => kind is
        BackendRuntimeFailureStatusKind.DatabaseCompatibility or
        BackendRuntimeFailureStatusKind.DatabaseConfigurationInvalid or
        BackendRuntimeFailureStatusKind.DatabaseKeyUnavailable or
        BackendRuntimeFailureStatusKind.DatabaseUnreadable;
}
