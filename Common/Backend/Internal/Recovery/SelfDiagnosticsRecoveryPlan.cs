namespace PasswordManagerLocal.Common.Backend.Internal.Recovery;

internal sealed class SelfDiagnosticsRecoveryPlan
{
    public UserRecoveryJournalSnapshot Archive { get; init; } = new();
    public List<string> Blockers { get; } = [];
    public List<string> Risks { get; } = [];
    public string PlanId { get; set; } = string.Empty;
    public long MaximumKeyEpoch { get; set; }
    public long HighestLocalSequence { get; set; }
    public int AffectedDevices { get; set; }
    public int PendingSnapshots { get; set; }
    public bool CanRecover => Blockers.Count == 0;
}
