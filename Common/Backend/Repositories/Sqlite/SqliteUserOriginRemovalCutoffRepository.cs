using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories.Sqlite;

internal sealed class SqliteUserOriginRemovalCutoffRepository(SqliteSession session) : IUserOriginRemovalCutoffRepository
{
    private const string Select = """
        SELECT "CutoffId", "AuthorizationId", "CreatedAtUtc", "DeviceId", "HighestAcceptedControlSequence",
               "HighestAcceptedSnapshotRevision", "OriginInstanceId", "RemovalOperationHash", "RemovalOperationId",
               "ResultingMembershipEpoch", "UserId", "UserKeyEpoch"
        FROM "UserOriginRemovalCutoffs"
        """;
    private const string Origin = """ "UserId" = $user AND "DeviceId" = $device AND "OriginInstanceId" = $instance """;

    private readonly SqliteRowWriter<UserOriginRemovalCutoff> _rows = new(session, "UserOriginRemovalCutoffs", 1,
    [
        (nameof(UserOriginRemovalCutoff.CutoffId), row => row.CutoffId),
        (nameof(UserOriginRemovalCutoff.AuthorizationId), row => row.AuthorizationId),
        (nameof(UserOriginRemovalCutoff.CreatedAtUtc), row => row.CreatedAtUtc),
        (nameof(UserOriginRemovalCutoff.DeviceId), row => row.DeviceId),
        (nameof(UserOriginRemovalCutoff.HighestAcceptedControlSequence), row => row.HighestAcceptedControlSequence),
        (nameof(UserOriginRemovalCutoff.HighestAcceptedSnapshotRevision), row => row.HighestAcceptedSnapshotRevision),
        (nameof(UserOriginRemovalCutoff.OriginInstanceId), row => row.OriginInstanceId),
        (nameof(UserOriginRemovalCutoff.RemovalOperationHash), row => row.RemovalOperationHash),
        (nameof(UserOriginRemovalCutoff.RemovalOperationId), row => row.RemovalOperationId),
        (nameof(UserOriginRemovalCutoff.ResultingMembershipEpoch), row => row.ResultingMembershipEpoch),
        (nameof(UserOriginRemovalCutoff.UserId), row => row.UserId),
        (nameof(UserOriginRemovalCutoff.UserKeyEpoch), row => row.UserKeyEpoch)
    ], trackChanges: false);

    public async Task<UserOriginRemovalCutoff?> GetAsync(
        Guid userId, Guid deviceId, Guid originInstanceId, long userKeyEpoch, CancellationToken ct = default) =>
        (await session.ReadAsync($""" {Select} WHERE {Origin} AND "UserKeyEpoch" = $epoch; """,
            ReadForWrite, ct, ("$user", userId), ("$device", deviceId), ("$instance", originInstanceId),
            ("$epoch", userKeyEpoch))).SingleOrDefault();

    public async Task<IReadOnlyList<UserOriginRemovalCutoff>> ListForUserAsync(Guid userId, CancellationToken ct = default) =>
        await session.ReadAsync($"""
            {Select} WHERE "UserId" = $user ORDER BY "ResultingMembershipEpoch", "DeviceId", "OriginInstanceId", "UserKeyEpoch";
            """, ReadForWrite, ct, ("$user", userId));

    public async Task<IReadOnlyList<UserOriginRemovalCutoff>> ListForOriginAsync(
        Guid userId, Guid deviceId, Guid originInstanceId, CancellationToken ct = default) =>
        await session.ReadAsync($""" {Select} WHERE {Origin} ORDER BY "UserKeyEpoch"; """,
            ReadForWrite, ct, ("$user", userId), ("$device", deviceId), ("$instance", originInstanceId));

    public Task AddAsync(UserOriginRemovalCutoff cutoff, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _rows.Stage(cutoff, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    public void DeleteRange(IEnumerable<UserOriginRemovalCutoff> cutoffs)
    {
        foreach (var row in cutoffs)
            _rows.Stage(row, SqliteMutation.Operation.Delete);
    }

    private UserOriginRemovalCutoff ReadForWrite(SqliteDataReader row) => _rows.Remember(new UserOriginRemovalCutoff
    {
        CutoffId = row.GetGuid(0),
        AuthorizationId = row.GetGuid(1),
        CreatedAtUtc = row.UtcOffset(2),
        DeviceId = row.GetGuid(3),
        HighestAcceptedControlSequence = row.GetInt64(4),
        HighestAcceptedSnapshotRevision = row.GetInt64(5),
        OriginInstanceId = row.GetGuid(6),
        RemovalOperationHash = row.Blob(7),
        RemovalOperationId = row.GetGuid(8),
        ResultingMembershipEpoch = row.GetInt64(9),
        UserId = row.GetGuid(10),
        UserKeyEpoch = row.GetInt64(11)
    });
}
