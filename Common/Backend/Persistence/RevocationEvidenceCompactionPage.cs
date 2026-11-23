using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Persistence;

internal sealed record RevocationEvidenceCompactionPage(
    IReadOnlyList<RevocationReceiptEvidence> Candidates, string? NextKey);
