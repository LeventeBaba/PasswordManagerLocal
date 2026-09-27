namespace PasswordManagerLocal.Common.Contracts.Runtime;

public sealed record BackendRuntimeSnapshot(
    BackendRuntimeState State,
    BackendRuntimeFailureKind FailureKind,
    Exception? Failure,
    DateTimeOffset ChangedAtUtc)
{
    public bool IsReady => State == BackendRuntimeState.Ready;
}
