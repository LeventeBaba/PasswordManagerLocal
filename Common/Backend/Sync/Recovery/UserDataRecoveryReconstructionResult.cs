using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;

namespace PasswordManagerLocal.Common.Backend.Sync.Recovery;

public sealed record UserDataRecoveryReconstructionResult(
    bool Reconstructed,
    IReadOnlyList<RecoveryCandidateVerificationResult> Candidates,
    string? DiagnosticCode = null)
{
    public int HealthyCandidateCount => Candidates.Count(candidate => candidate.IsHealthy);
    public UserDataBlobKind RecoveredComponents { get; init; } = UserDataBlobKind.None;
    public bool UsedVerifiedCache { get; init; }
}
