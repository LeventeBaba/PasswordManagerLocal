namespace PasswordManagerLocal.Common.Contracts.Responses;

public sealed class SelfDiagnosticsResultResponse
{
    public bool Healthy { get; set; }
    public int RepairedCount { get; set; }
    public IReadOnlyList<string> Findings { get; set; } = [];
    public string? RecoveryPlanId { get; set; }
    public IReadOnlyList<string> RecoveryRisks { get; set; } = [];
    public Guid? RecoveryJournalId { get; set; }
    public int RecoveryAffectedDevices { get; set; }
    public int RecoveryPendingSnapshots { get; set; }
}
