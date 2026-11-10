namespace PasswordManagerLocal.Common.Backend.Models;

public sealed record InteractiveSessionLifecycleSnapshot(
    InteractiveSessionLifecycleState State,
    Exception? Failure,
    DateTimeOffset ChangedAtUtc)
{
    public bool AcceptsOperations => State == InteractiveSessionLifecycleState.Active;
    public bool RequiresRecovery => State == InteractiveSessionLifecycleState.CleanupFailed;
}
