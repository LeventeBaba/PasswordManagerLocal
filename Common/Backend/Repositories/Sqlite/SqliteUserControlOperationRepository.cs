using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories.Sqlite;

internal sealed class SqliteUserControlOperationRepository(SqliteSession session) : IUserControlOperationRepository
{
    private const string Select = """
        SELECT "OperationId", "AppliedAtUtc", "ConflictingOperationHash", "CreatedAtUtc", "EnvelopePayload",
               "LastReceivedFromDeviceId", "OperationHash", "OperationType", "OriginDeviceId", "OriginInstanceId",
               "OriginSequence", "OriginSignPublicKey", "OriginSignature", "PayloadHash", "PreviousKeyEpoch",
               "PreviousMembershipEpoch", "ReceivedAtUtc", "ResultingKeyEpoch", "ResultingMembershipEpoch",
               "Status", "StatusReason", "UserId"
        FROM "UserControlOperations"
        """;
    private const string Origin = """
        "UserId" = $user AND "OriginDeviceId" = $device AND "OriginInstanceId" = $instance
        """;
    private const string AppliedDeletion = """ "OperationType" = $deletion AND "Status" = $applied """;
    private const string AppliedRevocation = """ "OperationType" IN ($removal, $deletion) AND "Status" = $applied """;

    private readonly SqliteRowWriter<UserControlOperation> _rows = new(session, "UserControlOperations", 1,
    [
        (nameof(UserControlOperation.OperationId), row => row.OperationId),
        (nameof(UserControlOperation.AppliedAtUtc), row => row.AppliedAtUtc),
        (nameof(UserControlOperation.ConflictingOperationHash), row => row.ConflictingOperationHash),
        (nameof(UserControlOperation.CreatedAtUtc), row => row.CreatedAtUtc),
        (nameof(UserControlOperation.EnvelopePayload), row => row.EnvelopePayload),
        (nameof(UserControlOperation.LastReceivedFromDeviceId), row => row.LastReceivedFromDeviceId),
        (nameof(UserControlOperation.OperationHash), row => row.OperationHash),
        (nameof(UserControlOperation.OperationType), row => (byte)row.OperationType),
        (nameof(UserControlOperation.OriginDeviceId), row => row.OriginDeviceId),
        (nameof(UserControlOperation.OriginInstanceId), row => row.OriginInstanceId),
        (nameof(UserControlOperation.OriginSequence), row => row.OriginSequence),
        (nameof(UserControlOperation.OriginSignPublicKey), row => row.OriginSignPublicKey),
        (nameof(UserControlOperation.OriginSignature), row => row.OriginSignature),
        (nameof(UserControlOperation.PayloadHash), row => row.PayloadHash),
        (nameof(UserControlOperation.PreviousKeyEpoch), row => row.PreviousKeyEpoch),
        (nameof(UserControlOperation.PreviousMembershipEpoch), row => row.PreviousMembershipEpoch),
        (nameof(UserControlOperation.ReceivedAtUtc), row => row.ReceivedAtUtc),
        (nameof(UserControlOperation.ResultingKeyEpoch), row => row.ResultingKeyEpoch),
        (nameof(UserControlOperation.ResultingMembershipEpoch), row => row.ResultingMembershipEpoch),
        (nameof(UserControlOperation.Status), row => (byte)row.Status),
        (nameof(UserControlOperation.StatusReason), row => row.StatusReason),
        (nameof(UserControlOperation.UserId), row => row.UserId)
    ], [nameof(UserControlOperation.OperationHash)], trackChanges: false);

    public async Task<UserControlOperation?> GetByIdAsync(Guid operationId, CancellationToken ct = default) =>
        (await session.ReadAsync($""" {Select} WHERE "OperationId" = $id; """,
            ReadForWrite, ct, ("$id", operationId))).SingleOrDefault();

    public async Task<UserControlOperation?> GetByOriginSequenceAsync(
        Guid userId, Guid originDeviceId, Guid originInstanceId, long originSequence, CancellationToken ct = default) =>
        (await session.ReadAsync($""" {Select} WHERE {Origin} AND "OriginSequence" = $sequence; """,
            ReadForWrite, ct, ("$user", userId), ("$device", originDeviceId), ("$instance", originInstanceId),
            ("$sequence", originSequence))).SingleOrDefault();

    public async Task<IReadOnlyList<UserControlOperation>> ListForUserAsync(Guid userId, CancellationToken ct = default) =>
        (await session.ReadAsync($""" {Select} WHERE "UserId" = $user; """, ReadForWrite, ct, ("$user", userId)))
            .OrderBy(operation => operation.CreatedAtUtc)
            .ThenBy(operation => operation.OriginDeviceId)
            .ThenBy(operation => operation.OriginInstanceId)
            .ThenBy(operation => operation.OriginSequence)
            .ToArray();

    public async Task<IReadOnlyList<UserControlOperation>> ListAllRelayableAsync(CancellationToken ct = default) =>
        await session.ReadAsync($"""
            {Select} WHERE "Status" <> $rejected
            ORDER BY "UserId", "OriginDeviceId", "OriginInstanceId", "OriginSequence";
            """, ReadForWrite, ct, ("$rejected", (byte)UserControlOperationStatus.Rejected));

    public Task<bool> HasAppliedAccountDeletionAsync(CancellationToken ct = default) =>
        session.ExistsAsync($""" SELECT 1 FROM "UserControlOperations" WHERE {AppliedDeletion} """, ct,
            ("$deletion", (byte)UserControlOperationType.AccountDeletion), ("$applied", (byte)UserControlOperationStatus.Applied));

    public async Task<IReadOnlyList<Guid>> ListAppliedAccountDeletionUserIdsAsync(CancellationToken ct = default) =>
        await session.ReadAsync($"""
            SELECT DISTINCT "UserId" FROM "UserControlOperations" WHERE {AppliedDeletion} ORDER BY "UserId";
            """, row => row.GetGuid(0), ct, ("$deletion", (byte)UserControlOperationType.AccountDeletion),
            ("$applied", (byte)UserControlOperationStatus.Applied));

    public Task<bool> HasAppliedRevocationAsync(CancellationToken ct = default) =>
        session.ExistsAsync($""" SELECT 1 FROM "UserControlOperations" WHERE {AppliedRevocation} """, ct,
            ("$removal", (byte)UserControlOperationType.DeviceRemoval), ("$deletion", (byte)UserControlOperationType.AccountDeletion),
            ("$applied", (byte)UserControlOperationStatus.Applied));

    public async Task<IReadOnlyList<Guid>> ListAppliedRevocationUserIdsAsync(CancellationToken ct = default) =>
        await session.ReadAsync($"""
            SELECT DISTINCT "UserId" FROM "UserControlOperations" WHERE {AppliedRevocation} ORDER BY "UserId";
            """, row => row.GetGuid(0), ct, ("$removal", (byte)UserControlOperationType.DeviceRemoval),
            ("$deletion", (byte)UserControlOperationType.AccountDeletion), ("$applied", (byte)UserControlOperationStatus.Applied));

    public async Task<IReadOnlyList<UserControlOperation>> ListPendingAsync(CancellationToken ct = default) =>
        (await session.ReadAsync($""" {Select} WHERE "Status" = $pending; """,
            Read, ct, ("$pending", (byte)UserControlOperationStatus.StoredPending)))
            .OrderBy(operation => operation.ReceivedAtUtc)
            .ThenBy(operation => operation.OperationId)
            .ToArray();

    public async Task<IReadOnlyList<UserControlOperation>> ListKeyTransitionsFromAsync(
        Guid userId, long previousKeyEpoch, CancellationToken ct = default) =>
        await session.ReadAsync($"""
            {Select} WHERE "UserId" = $user AND "OperationType" = $replacement
                AND "PreviousKeyEpoch" = $epoch AND "Status" <> $rejected
            ORDER BY "OperationId";
            """, ReadForWrite, ct, ("$user", userId), ("$replacement", (byte)UserControlOperationType.KeyEpochReplacement),
            ("$epoch", previousKeyEpoch), ("$rejected", (byte)UserControlOperationStatus.Rejected));

    public async Task<IReadOnlyList<UserControlOperation>> ListMembershipTransitionsFromAsync(
        Guid userId, long previousMembershipEpoch, CancellationToken ct = default) =>
        await session.ReadAsync($"""
            {Select} WHERE "UserId" = $user AND "OperationType" IN ($addition, $removal)
                AND "PreviousMembershipEpoch" = $epoch AND "Status" <> $rejected
            ORDER BY "OperationId";
            """, ReadForWrite, ct, ("$user", userId), ("$addition", (byte)UserControlOperationType.DeviceAddition),
            ("$removal", (byte)UserControlOperationType.DeviceRemoval), ("$epoch", previousMembershipEpoch),
            ("$rejected", (byte)UserControlOperationStatus.Rejected));

    public async Task<long> GetHighestOriginSequenceAsync(
        Guid userId, Guid originDeviceId, Guid originInstanceId, CancellationToken ct = default) =>
        (await session.ReadAsync($"""
            SELECT COALESCE(MAX("OriginSequence"), 0) FROM "UserControlOperations" WHERE {Origin};
            """, row => row.GetInt64(0), ct, ("$user", userId), ("$device", originDeviceId),
            ("$instance", originInstanceId))).Single();

    public void Detach(UserControlOperation operation) => session.DiscardPendingRow(operation);

    public Task AddAsync(UserControlOperation operation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _rows.Stage(operation, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    public void Update(UserControlOperation operation) => _rows.Stage(operation, SqliteMutation.Operation.Update);

    public void DeleteRange(IEnumerable<UserControlOperation> operations)
    {
        foreach (var row in operations)
            _rows.Stage(row, SqliteMutation.Operation.Delete);
    }

    private UserControlOperation ReadForWrite(SqliteDataReader row) => _rows.Remember(Read(row));

    private static UserControlOperation Read(SqliteDataReader row) => new()
    {
        OperationId = row.GetGuid(0),
        AppliedAtUtc = row.NullableUtcOffset(1),
        ConflictingOperationHash = row.NullableBlob(2),
        CreatedAtUtc = row.UtcOffset(3),
        EnvelopePayload = row.Blob(4),
        LastReceivedFromDeviceId = row.NullableGuid(5),
        OperationHash = row.Blob(6),
        OperationType = (UserControlOperationType)row.GetByte(7),
        OriginDeviceId = row.GetGuid(8),
        OriginInstanceId = row.GetGuid(9),
        OriginSequence = row.GetInt64(10),
        OriginSignPublicKey = row.Blob(11),
        OriginSignature = row.Blob(12),
        PayloadHash = row.Blob(13),
        PreviousKeyEpoch = row.GetInt64(14),
        PreviousMembershipEpoch = row.GetInt64(15),
        ReceivedAtUtc = row.UtcOffset(16),
        ResultingKeyEpoch = row.GetInt64(17),
        ResultingMembershipEpoch = row.GetInt64(18),
        Status = (UserControlOperationStatus)row.GetByte(19),
        StatusReason = row.NullableText(20),
        UserId = row.GetGuid(21)
    };
}
