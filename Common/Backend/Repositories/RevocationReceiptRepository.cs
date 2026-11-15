using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

internal sealed class RevocationReceiptRepository(SqliteSession session) : IRevocationReceiptRepository
{
    private readonly SqliteRowWriter<RevocationReceiptEvidence> _evidence = new(session, "RevocationReceiptEvidence", 1,
    [
        (nameof(RevocationReceiptEvidence.OperationId), r => r.OperationId),
        (nameof(RevocationReceiptEvidence.UserId), r => r.UserId),
        (nameof(RevocationReceiptEvidence.OperationHash), r => r.OperationHash),
        (nameof(RevocationReceiptEvidence.EnvelopePayload), r => r.EnvelopePayload),
        (nameof(RevocationReceiptEvidence.AuthorizationPayload), r => r.AuthorizationPayload),
        (nameof(RevocationReceiptEvidence.MembershipProofPayload), r => r.MembershipProofPayload),
        (nameof(RevocationReceiptEvidence.HistoryResetBlocked), r => r.HistoryResetBlocked),
        (nameof(RevocationReceiptEvidence.HasReceiptConflict), r => r.HasReceiptConflict)
    ]);
    private readonly SqliteRowWriter<StoredRevocationReceipt> _receipts = new(session, "RevocationDeliveryReceipts", 6,
    [
        (nameof(StoredRevocationReceipt.OperationId), r => r.OperationId),
        (nameof(StoredRevocationReceipt.RecipientDeviceId), r => r.RecipientDeviceId),
        (nameof(StoredRevocationReceipt.RecipientOriginInstanceId), r => r.RecipientOriginInstanceId),
        (nameof(StoredRevocationReceipt.RecipientAuthorizationId), r => r.RecipientAuthorizationId),
        (nameof(StoredRevocationReceipt.Kind), r => (byte)r.Kind),
        (nameof(StoredRevocationReceipt.AttestedReceiptHash), r => r.AttestedReceiptHash),
        (nameof(StoredRevocationReceipt.UserId), r => r.UserId),
        (nameof(StoredRevocationReceipt.ReceiptHash), r => r.ReceiptHash),
        (nameof(StoredRevocationReceipt.Payload), r => r.Payload)
    ]);

    private readonly SqliteRowWriter<RevocationEvidenceDebt> _debts = new(session, "RevocationEvidenceDebts", 5,
    [
        (nameof(RevocationEvidenceDebt.OperationId), r => r.OperationId),
        (nameof(RevocationEvidenceDebt.PeerDeviceId), r => r.PeerDeviceId),
        (nameof(RevocationEvidenceDebt.PeerOriginInstanceId), r => r.PeerOriginInstanceId),
        (nameof(RevocationEvidenceDebt.PeerAuthorizationId), r => r.PeerAuthorizationId),
        (nameof(RevocationEvidenceDebt.ReceiptHash), r => r.ReceiptHash)
    ]);

    public async Task<IReadOnlyList<RevocationEvidenceDebt>> ListDebtsAsync(Guid operationId, CancellationToken ct = default) =>
        await session.ReadAsync("""
            SELECT "OperationId", "PeerDeviceId", "PeerOriginInstanceId", "PeerAuthorizationId", "ReceiptHash"
            FROM "RevocationEvidenceDebts" WHERE "OperationId" = $id;
            """, row => _debts.Remember(new RevocationEvidenceDebt
            {
                OperationId = row.GetGuid(0), PeerDeviceId = row.GetGuid(1), PeerOriginInstanceId = row.GetGuid(2),
                PeerAuthorizationId = row.GetGuid(3), ReceiptHash = row.Blob(4)
            }), ct, ("$id", operationId));
    public Task AddDebtAsync(RevocationEvidenceDebt debt, CancellationToken ct = default)
    {
        RequireTransaction(ct); _debts.Stage(debt, SqliteMutation.Operation.Insert); return Task.CompletedTask;
    }
    public void DeleteDebt(RevocationEvidenceDebt debt)
    {
        RequireTransaction(default); _debts.Stage(debt, SqliteMutation.Operation.Delete);
    }

    private RevocationReceiptEvidence ReadEvidence(Microsoft.Data.Sqlite.SqliteDataReader row) => _evidence.Remember(new()
    {
        OperationId = row.GetGuid(0), UserId = row.GetGuid(1), OperationHash = row.Blob(2),
        EnvelopePayload = row.Blob(3), AuthorizationPayload = row.Blob(4),
        HistoryResetBlocked = row.GetBoolean(5), HasReceiptConflict = row.GetBoolean(6), MembershipProofPayload = row.Blob(7)
    });

    public async Task<IReadOnlyList<RevocationReceiptEvidence>> ListEvidenceAsync(CancellationToken ct = default) =>
        await session.ReadAsync("""
            SELECT "OperationId", "UserId", "OperationHash", "EnvelopePayload", "AuthorizationPayload", "HistoryResetBlocked", "HasReceiptConflict", "MembershipProofPayload"
            FROM "RevocationReceiptEvidence" ORDER BY "UserId", "OperationId";
            """, ReadEvidence, ct);

    public void UpdateEvidence(RevocationReceiptEvidence evidence)
    {
        RequireTransaction(default);
        _evidence.Stage(evidence, SqliteMutation.Operation.Update);
    }

    public async Task<RevocationReceiptEvidence?> GetEvidenceAsync(Guid operationId, CancellationToken ct = default) =>
        (await session.ReadAsync("""
            SELECT "OperationId", "UserId", "OperationHash", "EnvelopePayload", "AuthorizationPayload", "HistoryResetBlocked", "HasReceiptConflict", "MembershipProofPayload"
            FROM "RevocationReceiptEvidence" WHERE "OperationId" = $id;
            """, ReadEvidence, ct, ("$id", operationId))).SingleOrDefault();

    public async Task<IReadOnlyList<StoredRevocationReceipt>> ListAsync(Guid operationId, CancellationToken ct = default) =>
        await session.ReadAsync("""
            SELECT "OperationId", "RecipientDeviceId", "RecipientOriginInstanceId", "RecipientAuthorizationId",
                   "Kind", "AttestedReceiptHash", "UserId", "ReceiptHash", "Payload"
            FROM "RevocationDeliveryReceipts" WHERE "OperationId" = $id
            ORDER BY "RecipientDeviceId", "RecipientOriginInstanceId", "RecipientAuthorizationId", "Kind";
            """, r => new StoredRevocationReceipt
            {
                OperationId = r.GetGuid(0), RecipientDeviceId = r.GetGuid(1), RecipientOriginInstanceId = r.GetGuid(2),
                RecipientAuthorizationId = r.GetGuid(3), Kind = (RevocationReceiptKind)r.GetByte(4),
                AttestedReceiptHash = r.Blob(5), UserId = r.GetGuid(6), ReceiptHash = r.Blob(7), Payload = r.Blob(8)
            }, ct, ("$id", operationId));

    public Task AddEvidenceAsync(RevocationReceiptEvidence evidence, CancellationToken ct = default)
    {
        RequireTransaction(ct);
        _evidence.Stage(evidence, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    public Task AddAsync(StoredRevocationReceipt receipt, CancellationToken ct = default)
    {
        RequireTransaction(ct);
        _receipts.Stage(receipt, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    private void RequireTransaction(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!session.HasActiveTransaction)
            throw new InvalidOperationException("Revocation evidence requires an explicit application/import transaction.");
    }
}
