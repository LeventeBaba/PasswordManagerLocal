using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories.Sqlite;

internal sealed class SqliteUserRevisionKnowledgeRepository(SqliteSession session) : IUserRevisionKnowledgeRepository
{
    internal const string Select = """
        SELECT "UserId", "OriginDeviceId", "OriginInstanceId", "UserKeyEpoch", "HighestMergedRevision",
               "HighestStoredRevision", "HighestStoredSnapshotHash", "LastUpdatedAtUtc" FROM "UserRevisionKnowledge"
        """;

    private readonly SqliteRowWriter<UserRevisionKnowledge> _rows = new(session, "UserRevisionKnowledge", 4,
    [
        (nameof(UserRevisionKnowledge.UserId), row => row.UserId),
        (nameof(UserRevisionKnowledge.OriginDeviceId), row => row.OriginDeviceId),
        (nameof(UserRevisionKnowledge.OriginInstanceId), row => row.OriginInstanceId),
        (nameof(UserRevisionKnowledge.UserKeyEpoch), row => row.UserKeyEpoch),
        (nameof(UserRevisionKnowledge.HighestMergedRevision), row => row.HighestMergedRevision),
        (nameof(UserRevisionKnowledge.HighestStoredRevision), row => row.HighestStoredRevision),
        (nameof(UserRevisionKnowledge.HighestStoredSnapshotHash), row => row.HighestStoredSnapshotHash),
        (nameof(UserRevisionKnowledge.LastUpdatedAtUtc), row => row.LastUpdatedAtUtc)
    ], trackChanges: false);

    public async Task<UserRevisionKnowledge?> GetAsync(Guid userId, Guid originDeviceId, Guid originInstanceId,
        long userKeyEpoch, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var pending = session.GetPendingRows<UserRevisionKnowledge>().LastOrDefault(item =>
            item.Row.UserId == userId && item.Row.OriginDeviceId == originDeviceId &&
            item.Row.OriginInstanceId == originInstanceId && item.Row.UserKeyEpoch == userKeyEpoch);
        if (pending.Row is not null)
            return pending.Operation == SqliteMutation.Operation.Delete ? null : pending.Row;

        return (await session.ReadAsync($"""
            {Select} WHERE "UserId" = $user AND "OriginDeviceId" = $device
              AND "OriginInstanceId" = $instance AND "UserKeyEpoch" = $epoch;
            """, ReadForWrite, ct, ("$user", userId), ("$device", originDeviceId), ("$instance", originInstanceId),
            ("$epoch", userKeyEpoch))).SingleOrDefault();
    }

    public async Task<IReadOnlyList<UserRevisionKnowledge>> ListAsync(Guid userId, long userKeyEpoch, CancellationToken ct = default) =>
        await session.ReadAsync($"""
            {Select} WHERE "UserId" = $user AND "UserKeyEpoch" = $epoch
            ORDER BY "OriginDeviceId", "OriginInstanceId";
            """, ReadForWrite, ct, ("$user", userId), ("$epoch", userKeyEpoch));

    public async Task<IReadOnlyList<UserRevisionKnowledge>> ListForUserAsync(Guid userId, CancellationToken ct = default) =>
        await session.ReadAsync($"""
            {Select} WHERE "UserId" = $user ORDER BY "OriginDeviceId", "OriginInstanceId", "UserKeyEpoch";
            """, ReadForWrite, ct, ("$user", userId));

    public async Task<IReadOnlyList<UserRevisionKnowledge>> ListForOriginAsync(Guid userId, Guid originDeviceId,
        Guid originInstanceId, CancellationToken ct = default) =>
        await session.ReadAsync($"""
            {Select} WHERE "UserId" = $user AND "OriginDeviceId" = $device AND "OriginInstanceId" = $instance
            ORDER BY "UserKeyEpoch";
            """, ReadForWrite, ct, ("$user", userId), ("$device", originDeviceId), ("$instance", originInstanceId));

    public Task AddAsync(UserRevisionKnowledge knowledge, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _rows.Stage(knowledge, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    public void Update(UserRevisionKnowledge knowledge) => _rows.Stage(knowledge, SqliteMutation.Operation.Update);

    public void DeleteRange(IEnumerable<UserRevisionKnowledge> knowledge)
    {
        foreach (var row in knowledge)
            _rows.Stage(row, SqliteMutation.Operation.Delete);
    }

    private UserRevisionKnowledge ReadForWrite(SqliteDataReader row) => _rows.Remember(Read(row));

    internal static UserRevisionKnowledge Read(SqliteDataReader row) => new()
    {
        UserId = row.GetGuid(0),
        OriginDeviceId = row.GetGuid(1),
        OriginInstanceId = row.GetGuid(2),
        UserKeyEpoch = row.GetInt64(3),
        HighestMergedRevision = row.GetInt64(4),
        HighestStoredRevision = row.GetInt64(5),
        HighestStoredSnapshotHash = row.Blob(6),
        LastUpdatedAtUtc = row.UtcOffset(7)
    };
}
