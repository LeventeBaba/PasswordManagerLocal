namespace PasswordManagerLocal.Common.Backend.Models;

public sealed record SyncRuntimeSnapshot(
    SyncRuntimeState State,
    Exception? Failure,
    SyncRuntimeReason Reasons = SyncRuntimeReason.None,
    SyncRuntimeEligibilityReason Eligibility = SyncRuntimeEligibilityReason.NoLocalProfiles,
    bool RequiresProcessRestart = false,
    bool IsPassiveListening = false)
{
    public bool IsRetryable => State == SyncRuntimeState.Degraded && !RequiresProcessRestart;
}
