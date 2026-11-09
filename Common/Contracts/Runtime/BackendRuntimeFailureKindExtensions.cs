namespace PasswordManagerLocal.Common.Contracts.Runtime;

public static class BackendRuntimeFailureKindExtensions
{
    public static bool RequiresDatabaseReset(this BackendRuntimeFailureKind kind) => kind is
        BackendRuntimeFailureKind.DatabaseCompatibility or
        BackendRuntimeFailureKind.DatabaseConfigurationInvalid or
        BackendRuntimeFailureKind.DatabaseKeyUnavailable or
        BackendRuntimeFailureKind.DatabaseUnreadable;
}
