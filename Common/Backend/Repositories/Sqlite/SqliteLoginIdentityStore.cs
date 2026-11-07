using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories.Sqlite;

internal sealed class SqliteLoginIdentityStore(SqliteSession session)
{
    private const string Select = """
        SELECT "UserId", "ConcurrencyVersion", "KeyEpoch", "MembershipEpoch", "SourceOriginDeviceId",
               "SourceOriginInstanceId", "SourceOriginRevision", "SourceSnapshotHash", "Status", "StatusReason",
               "UpdatedAtUtc", "UsernameHash", "UsernameSalt", "VersionLogicalCounter", "VersionOriginDeviceId",
               "VersionOriginInstanceId", "VersionPhysicalTimeUnixMilliseconds"
        FROM "UserLoginIdentityStates"
        """;

    private readonly SqliteRowWriter<UserLoginIdentityState> _rows = new(session, "UserLoginIdentityStates", 1,
    [
        (nameof(UserLoginIdentityState.UserId), row => row.UserId),
        (nameof(UserLoginIdentityState.ConcurrencyVersion), row => row.ConcurrencyVersion),
        (nameof(UserLoginIdentityState.KeyEpoch), row => row.KeyEpoch),
        (nameof(UserLoginIdentityState.MembershipEpoch), row => row.MembershipEpoch),
        (nameof(UserLoginIdentityState.SourceOriginDeviceId), row => row.SourceOriginDeviceId),
        (nameof(UserLoginIdentityState.SourceOriginInstanceId), row => row.SourceOriginInstanceId),
        (nameof(UserLoginIdentityState.SourceOriginRevision), row => row.SourceOriginRevision),
        (nameof(UserLoginIdentityState.SourceSnapshotHash), row => row.SourceSnapshotHash),
        (nameof(UserLoginIdentityState.Status), row => (byte)row.Status),
        (nameof(UserLoginIdentityState.StatusReason), row => row.StatusReason),
        (nameof(UserLoginIdentityState.UpdatedAtUtc), row => row.UpdatedAtUtc),
        (nameof(UserLoginIdentityState.UsernameHash), row => row.UsernameHash),
        (nameof(UserLoginIdentityState.UsernameSalt), row => row.UsernameSalt),
        (nameof(UserLoginIdentityState.VersionLogicalCounter), row => row.VersionLogicalCounter),
        (nameof(UserLoginIdentityState.VersionOriginDeviceId), row => row.VersionOriginDeviceId),
        (nameof(UserLoginIdentityState.VersionOriginInstanceId), row => row.VersionOriginInstanceId),
        (nameof(UserLoginIdentityState.VersionPhysicalTimeUnixMilliseconds), row => row.VersionPhysicalTimeUnixMilliseconds)
    ], concurrencyColumns: [nameof(UserLoginIdentityState.ConcurrencyVersion)], userId: row => row.UserId);

    public async Task<IReadOnlyList<UserLoginLookupData>> ListLookupDataAsync(CancellationToken ct) =>
        await session.ReadAsync("""
            SELECT "UserId", "UsernameSalt", "UsernameHash", "Status", "KeyEpoch", "MembershipEpoch"
            FROM "UserLoginIdentityStates";
            """, row => new UserLoginLookupData
            {
                UId = row.GetGuid(0),
                UsernameSalt = row.Blob(1),
                UsernameHash = row.Blob(2),
                Status = (UserLoginIdentityStatus)row.GetByte(3),
                KeyEpoch = row.GetInt64(4),
                MembershipEpoch = row.GetInt64(5)
            }, ct);

    public async Task<IReadOnlyList<UserLoginIdentityState>> ListAsync(CancellationToken ct) =>
        await session.ReadAsync(Select, ReadRow, ct);

    public async Task<UserLoginIdentityState?> GetAsync(Guid userId, CancellationToken ct) =>
        (await session.ReadAsync($"""{Select} WHERE "UserId" = $id;""", row => _rows.Remember(ReadRow(row)),
            ct, ("$id", userId))).SingleOrDefault();

    public void Stage(UserLoginIdentityState state, SqliteMutation.Operation operation) => _rows.Stage(state, operation);

    private static UserLoginIdentityState ReadRow(SqliteDataReader row) => new()
    {
        UserId = row.GetGuid(0),
        ConcurrencyVersion = row.GetInt64(1),
        KeyEpoch = row.GetInt64(2),
        MembershipEpoch = row.GetInt64(3),
        SourceOriginDeviceId = row.GetGuid(4),
        SourceOriginInstanceId = row.GetGuid(5),
        SourceOriginRevision = row.GetInt64(6),
        SourceSnapshotHash = row.Blob(7),
        Status = (UserLoginIdentityStatus)row.GetByte(8),
        StatusReason = row.NullableText(9),
        UpdatedAtUtc = row.UtcOffset(10),
        UsernameHash = row.Blob(11),
        UsernameSalt = row.Blob(12),
        VersionLogicalCounter = row.GetInt64(13),
        VersionOriginDeviceId = row.GetGuid(14),
        VersionOriginInstanceId = row.GetGuid(15),
        VersionPhysicalTimeUnixMilliseconds = row.GetInt64(16)
    };
}
