namespace PasswordManagerLocal.Android.Runtime;

internal sealed record AndroidInteractiveCleanupResult(
    AndroidInteractiveCleanupOutcome Outcome,
    Exception? Failure)
{
    public bool IsRuntimeSafe => Outcome != AndroidInteractiveCleanupOutcome.RuntimeUnsafe;
}
