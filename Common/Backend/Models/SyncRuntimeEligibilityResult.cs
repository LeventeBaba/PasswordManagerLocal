namespace PasswordManagerLocal.Common.Backend.Models;

/// <summary>Contains no account identifiers, names, keys or endpoint/presence information.</summary>
public sealed record SyncRuntimeEligibilityResult(
    SyncRuntimeEligibilityReason Reason,
    bool HasHistoricalControlEvidence = false)
{
    public bool IsEligible => Reason == SyncRuntimeEligibilityReason.Eligible;
}
