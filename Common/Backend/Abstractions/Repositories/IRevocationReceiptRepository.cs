using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Repositories;

public interface IRevocationReceiptRepository
{
    Task<RevocationReceiptEvidence?> GetEvidenceAsync(Guid operationId, CancellationToken ct = default);
    Task<IReadOnlyList<StoredRevocationReceipt>> ListAsync(Guid operationId, CancellationToken ct = default);
    Task<IReadOnlyList<RevocationReceiptEvidence>> ListEvidenceAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RevocationEvidenceDebt>> ListDebtsAsync(Guid operationId, CancellationToken ct = default);
    Task AddDebtAsync(RevocationEvidenceDebt debt, CancellationToken ct = default);
    void DeleteDebt(RevocationEvidenceDebt debt);
    void UpdateEvidence(RevocationReceiptEvidence evidence);
    // Both writes require the caller's application/import transaction. Evidence has no deletion API.
    Task AddEvidenceAsync(RevocationReceiptEvidence evidence, CancellationToken ct = default);
    Task AddAsync(StoredRevocationReceipt receipt, CancellationToken ct = default);
}
