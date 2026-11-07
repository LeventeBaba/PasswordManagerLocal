using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories.Sqlite;

internal sealed class SqliteUserCanonicalCheckpointRepository(SqliteSession session) : IUserCanonicalCheckpointRepository
{
    private readonly SqliteRowWriter<UserCanonicalCheckpoint> _rows = new(session, "UserCanonicalCheckpoints", 1,
    [
        (nameof(UserCanonicalCheckpoint.UserId), row => row.UserId),
        (nameof(UserCanonicalCheckpoint.CanonicalContentHash), row => row.CanonicalContentHash),
        (nameof(UserCanonicalCheckpoint.CheckpointSequence), row => row.CheckpointSequence),
        (nameof(UserCanonicalCheckpoint.CreatedAtUtc), row => row.CreatedAtUtc),
        (nameof(UserCanonicalCheckpoint.KeyEpoch), row => row.KeyEpoch),
        (nameof(UserCanonicalCheckpoint.LocalDeviceId), row => row.LocalDeviceId),
        (nameof(UserCanonicalCheckpoint.LocalOriginInstanceId), row => row.LocalOriginInstanceId),
        (nameof(UserCanonicalCheckpoint.MembershipEpoch), row => row.MembershipEpoch),
        (nameof(UserCanonicalCheckpoint.SignPublicKey), row => row.SignPublicKey),
        (nameof(UserCanonicalCheckpoint.Signature), row => row.Signature),
        (nameof(UserCanonicalCheckpoint.UserIntegrityHash), row => row.UserIntegrityHash)
    ], concurrencyColumns: [nameof(UserCanonicalCheckpoint.CheckpointSequence), nameof(UserCanonicalCheckpoint.CanonicalContentHash)],
        trackChanges: false);

    public async Task<UserCanonicalCheckpoint?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // A checkpoint can be verified by the health gate before the transaction flushes.
        var pending = session.GetPendingRows<UserCanonicalCheckpoint>().LastOrDefault(item => item.Row.UserId == userId);
        if (pending.Row is not null)
            return pending.Operation == SqliteMutation.Operation.Delete ? null : pending.Row;

        return (await session.ReadAsync("""
            SELECT "UserId", "CanonicalContentHash", "CheckpointSequence", "CreatedAtUtc", "KeyEpoch", "LocalDeviceId",
                   "LocalOriginInstanceId", "MembershipEpoch", "SignPublicKey", "Signature", "UserIntegrityHash"
            FROM "UserCanonicalCheckpoints" WHERE "UserId" = $user;
            """, row => _rows.Remember(ReadRow(row)), ct, ("$user", userId))).SingleOrDefault();
    }

    public Task AddAsync(UserCanonicalCheckpoint checkpoint, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _rows.Stage(checkpoint, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    public void Update(UserCanonicalCheckpoint checkpoint) => _rows.Stage(checkpoint, SqliteMutation.Operation.Update);

    public void Delete(UserCanonicalCheckpoint checkpoint) => _rows.Stage(checkpoint, SqliteMutation.Operation.Delete);

    private static UserCanonicalCheckpoint ReadRow(SqliteDataReader row) => new()
    {
        UserId = row.GetGuid(0),
        CanonicalContentHash = row.Blob(1),
        CheckpointSequence = row.GetInt64(2),
        CreatedAtUtc = row.UtcOffset(3),
        KeyEpoch = row.GetInt64(4),
        LocalDeviceId = row.GetGuid(5),
        LocalOriginInstanceId = row.GetGuid(6),
        MembershipEpoch = row.GetInt64(7),
        SignPublicKey = row.Blob(8),
        Signature = row.Blob(9),
        UserIntegrityHash = row.Blob(10)
    };
}
