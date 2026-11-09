using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

internal sealed class DeletedUserBarrierRepository(SqliteSession session) : IDeletedUserBarrierRepository
{
    private const string Select = """
        SELECT "UserId", "AppliedAtUtc", "ConflictingOperationHash", "ConflictingOperationId", "DeletedAtUtc",
               "DeletionGeneration", "DeletionOperationId", "HasConflict", "KeyEpoch", "LastUpdatedAtUtc",
               "MembershipEpoch", "OperationHash", "OriginDeviceId", "OriginInstanceId", "OriginSequence",
               "OriginSignPublicKey", "OriginSignature"
        FROM "DeletedUserBarriers"
        """;

    private readonly SqliteRowWriter<DeletedUserBarrier> _rows = new(session, "DeletedUserBarriers", 1,
    [
        (nameof(DeletedUserBarrier.UserId), row => row.UserId),
        (nameof(DeletedUserBarrier.AppliedAtUtc), row => row.AppliedAtUtc),
        (nameof(DeletedUserBarrier.ConflictingOperationHash), row => row.ConflictingOperationHash),
        (nameof(DeletedUserBarrier.ConflictingOperationId), row => row.ConflictingOperationId),
        (nameof(DeletedUserBarrier.DeletedAtUtc), row => row.DeletedAtUtc),
        (nameof(DeletedUserBarrier.DeletionGeneration), row => row.DeletionGeneration),
        (nameof(DeletedUserBarrier.DeletionOperationId), row => row.DeletionOperationId),
        (nameof(DeletedUserBarrier.HasConflict), row => row.HasConflict),
        (nameof(DeletedUserBarrier.KeyEpoch), row => row.KeyEpoch),
        (nameof(DeletedUserBarrier.LastUpdatedAtUtc), row => row.LastUpdatedAtUtc),
        (nameof(DeletedUserBarrier.MembershipEpoch), row => row.MembershipEpoch),
        (nameof(DeletedUserBarrier.OperationHash), row => row.OperationHash),
        (nameof(DeletedUserBarrier.OriginDeviceId), row => row.OriginDeviceId),
        (nameof(DeletedUserBarrier.OriginInstanceId), row => row.OriginInstanceId),
        (nameof(DeletedUserBarrier.OriginSequence), row => row.OriginSequence),
        (nameof(DeletedUserBarrier.OriginSignPublicKey), row => row.OriginSignPublicKey),
        (nameof(DeletedUserBarrier.OriginSignature), row => row.OriginSignature)
    ], [nameof(DeletedUserBarrier.OperationHash)], userId: row => row.UserId);

    public async Task<DeletedUserBarrier?> GetAsync(Guid userId, CancellationToken ct = default) =>
        (await session.ReadAsync($""" {Select} WHERE "UserId" = $user; """,
            ReadForWrite, ct, ("$user", userId))).SingleOrDefault();

    public Task<bool> ExistsAsync(Guid userId, CancellationToken ct = default) =>
        session.ExistsAsync(""" SELECT 1 FROM "DeletedUserBarriers" WHERE "UserId" = $user """, ct, ("$user", userId));

    public async Task<IReadOnlyList<DeletedUserBarrier>> ListAllAsync(CancellationToken ct = default) =>
        await session.ReadAsync($""" {Select} ORDER BY "UserId"; """, Read, ct);

    public Task AddAsync(DeletedUserBarrier barrier, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _rows.Stage(barrier, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    public void Update(DeletedUserBarrier barrier) => _rows.Stage(barrier, SqliteMutation.Operation.Update);

    private DeletedUserBarrier ReadForWrite(SqliteDataReader row) => _rows.Remember(Read(row));

    private static DeletedUserBarrier Read(SqliteDataReader row) => new()
    {
        UserId = row.GetGuid(0),
        AppliedAtUtc = row.UtcOffset(1),
        ConflictingOperationHash = row.NullableBlob(2),
        ConflictingOperationId = row.NullableGuid(3),
        DeletedAtUtc = row.UtcOffset(4),
        DeletionGeneration = row.GetGuid(5),
        DeletionOperationId = row.GetGuid(6),
        HasConflict = row.GetBoolean(7),
        KeyEpoch = row.GetInt64(8),
        LastUpdatedAtUtc = row.UtcOffset(9),
        MembershipEpoch = row.GetInt64(10),
        OperationHash = row.Blob(11),
        OriginDeviceId = row.GetGuid(12),
        OriginInstanceId = row.GetGuid(13),
        OriginSequence = row.GetInt64(14),
        OriginSignPublicKey = row.Blob(15),
        OriginSignature = row.Blob(16)
    };
}
