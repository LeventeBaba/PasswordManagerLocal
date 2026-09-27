namespace PasswordManagerLocal.Common.Backend.Sync.Tcp;

public enum SyncProtocolStatusCode
{
    Unknown,
    Unauthenticated,
    Unavailable,
    InvalidArgument,
    PermissionDenied,
    ResourceExhausted,
    FailedPrecondition
}
