namespace PasswordManagerLocal.Common.Backend.Models;

public enum SyncRuntimeEligibilityReason
{
    Eligible,
    NoLocalProfiles,
    LocalSynchronizationDisabled,
    NoRemoteDevices,
    NoEligibleRemoteDevices,
    EvaluationFailed
}
