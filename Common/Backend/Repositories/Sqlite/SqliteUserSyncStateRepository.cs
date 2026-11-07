using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories.Sqlite;

internal sealed class SqliteUserSyncStateRepository(SqliteSession session) : IUserSyncStateRepository
{
    private readonly SqliteRowWriter<UserSyncState> _rows = new(session, "UserSyncStates", 1,
    [
        (nameof(UserSyncState.UserId), row => row.UserId),
        (nameof(UserSyncState.LastPublishedContentHash), row => row.LastPublishedContentHash),
        (nameof(UserSyncState.LastUpdatedAtUtc), row => row.LastUpdatedAtUtc),
        (nameof(UserSyncState.LocalOriginInstanceId), row => row.LocalOriginInstanceId),
        (nameof(UserSyncState.NextOriginRevision), row => row.NextOriginRevision)
    ], concurrencyColumns: [nameof(UserSyncState.NextOriginRevision), nameof(UserSyncState.LastPublishedContentHash)],
        trackChanges: false);

    public async Task<UserSyncState?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var pending = session.GetPendingRows<UserSyncState>().FirstOrDefault(item => item.Row.UserId == userId);
        if (pending.Row is not null)
            return pending.Row;

        return (await session.ReadAsync("""
            SELECT "UserId", "LastPublishedContentHash", "LastUpdatedAtUtc", "LocalOriginInstanceId", "NextOriginRevision"
            FROM "UserSyncStates" WHERE "UserId" = $user;
            """, row => _rows.Remember(ReadRow(row)), ct, ("$user", userId))).SingleOrDefault();
    }

    public Task AddAsync(UserSyncState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _rows.Stage(state, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    public void Update(UserSyncState state) => _rows.Stage(state, SqliteMutation.Operation.Update);

    private static UserSyncState ReadRow(SqliteDataReader row) => new()
    {
        UserId = row.GetGuid(0),
        LastPublishedContentHash = row.Blob(1),
        LastUpdatedAtUtc = row.UtcOffset(2),
        LocalOriginInstanceId = row.GetGuid(3),
        NextOriginRevision = row.GetInt64(4)
    };
}
