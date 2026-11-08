using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

internal sealed class UserControlStateRepository(SqliteSession session) : IUserControlStateRepository
{
    private const string Select = """
        SELECT "UserId", "AppliedKeyEpoch", "AppliedMembershipEpoch", "ConflictReason", "ConflictingOperationHash",
               "ConflictingOperationId", "HasConflict", "LastUpdatedAtUtc", "LocalOriginInstanceId",
               "NextOriginSequence"
        FROM "UserControlStates"
        """;

    private readonly SqliteRowWriter<UserControlState> _rows = new(session, "UserControlStates", 1,
    [
        (nameof(UserControlState.UserId), row => row.UserId),
        (nameof(UserControlState.AppliedKeyEpoch), row => row.AppliedKeyEpoch),
        (nameof(UserControlState.AppliedMembershipEpoch), row => row.AppliedMembershipEpoch),
        (nameof(UserControlState.ConflictReason), row => row.ConflictReason),
        (nameof(UserControlState.ConflictingOperationHash), row => row.ConflictingOperationHash),
        (nameof(UserControlState.ConflictingOperationId), row => row.ConflictingOperationId),
        (nameof(UserControlState.HasConflict), row => row.HasConflict),
        (nameof(UserControlState.LastUpdatedAtUtc), row => row.LastUpdatedAtUtc),
        (nameof(UserControlState.LocalOriginInstanceId), row => row.LocalOriginInstanceId),
        (nameof(UserControlState.NextOriginSequence), row => row.NextOriginSequence)
    ], trackChanges: false);

    public async Task<UserControlState?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var pending = session.GetPendingRows<UserControlState>().LastOrDefault(item => item.Row.UserId == userId);
        if (pending.Row is not null)
            return pending.Row;

        return (await session.ReadAsync($""" {Select} WHERE "UserId" = $user; """,
            ReadForWrite, ct, ("$user", userId))).SingleOrDefault();
    }

    public Task AddAsync(UserControlState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _rows.Stage(state, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    public void Update(UserControlState state) => _rows.Stage(state, SqliteMutation.Operation.Update);

    private UserControlState ReadForWrite(SqliteDataReader row) => _rows.Remember(new UserControlState
    {
        UserId = row.GetGuid(0),
        AppliedKeyEpoch = row.GetInt64(1),
        AppliedMembershipEpoch = row.GetInt64(2),
        ConflictReason = row.NullableText(3),
        ConflictingOperationHash = row.NullableBlob(4),
        ConflictingOperationId = row.NullableGuid(5),
        HasConflict = row.GetBoolean(6),
        LastUpdatedAtUtc = row.UtcOffset(7),
        LocalOriginInstanceId = row.GetGuid(8),
        NextOriginSequence = row.GetInt64(9)
    });
}
