using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories.Sqlite;

internal sealed class SqliteUserRepository(SqliteSession session) : IUserRepository
{
    internal const string Projection = """
        u."UId", u."EncryptedGeneralUserDataPayload", u."EncryptedPayload", u."EncryptedUserDevicesDataPayload",
        u."EncryptedUserPasswordsDataPayload", u."GeneralDataVersionLogicalCounter", u."GeneralDataVersionOriginDeviceId",
        u."GeneralDataVersionOriginInstanceId", u."GeneralDataVersionPhysicalTimeUnixMilliseconds",
        u."GeneralUserDataLastModifiedAt", u."IntegrityHash", u."KeyEpoch", u."LastModifiedAt", u."MembershipEpoch",
        u."PasswordSalt", u."SavedKey", u."UserDataLastModifiedAt", u."UserDevicesDataLastModifiedAt",
        u."UserPasswordsDataLastModifiedAt", u."UsernameHash", u."UsernameSalt"
        """;
    private const string Select = $"""SELECT {Projection} FROM "Users" u""";

    private readonly SqliteLoginIdentityStore _login = new(session);
    private readonly SqliteRowWriter<User> _rows = new(session, "Users", 1,
    [
        (nameof(User.UId), row => row.UId),
        (nameof(User.EncryptedGeneralUserDataPayload), row => row.EncryptedGeneralUserDataPayload),
        (nameof(User.EncryptedPayload), row => row.EncryptedPayload),
        (nameof(User.EncryptedUserDevicesDataPayload), row => row.EncryptedUserDevicesDataPayload),
        (nameof(User.EncryptedUserPasswordsDataPayload), row => row.EncryptedUserPasswordsDataPayload),
        (nameof(User.GeneralDataVersionLogicalCounter), row => row.GeneralDataVersionLogicalCounter),
        (nameof(User.GeneralDataVersionOriginDeviceId), row => row.GeneralDataVersionOriginDeviceId),
        (nameof(User.GeneralDataVersionOriginInstanceId), row => row.GeneralDataVersionOriginInstanceId),
        (nameof(User.GeneralDataVersionPhysicalTimeUnixMilliseconds), row => row.GeneralDataVersionPhysicalTimeUnixMilliseconds),
        (nameof(User.GeneralUserDataLastModifiedAt), row => row.GeneralUserDataLastModifiedAt),
        (nameof(User.IntegrityHash), row => row.IntegrityHash),
        (nameof(User.KeyEpoch), row => row.KeyEpoch),
        (nameof(User.LastModifiedAt), row => row.LastModifiedAt),
        (nameof(User.MembershipEpoch), row => row.MembershipEpoch),
        (nameof(User.PasswordSalt), row => row.PasswordSalt),
        (nameof(User.SavedKey), row => row.SavedKey),
        (nameof(User.UserDataLastModifiedAt), row => row.UserDataLastModifiedAt),
        (nameof(User.UserDevicesDataLastModifiedAt), row => row.UserDevicesDataLastModifiedAt),
        (nameof(User.UserPasswordsDataLastModifiedAt), row => row.UserPasswordsDataLastModifiedAt),
        (nameof(User.UsernameHash), row => row.UsernameHash),
        (nameof(User.UsernameSalt), row => row.UsernameSalt)
    ], concurrencyColumns:
    [
        nameof(User.EncryptedPayload),
        nameof(User.EncryptedGeneralUserDataPayload),
        nameof(User.EncryptedUserPasswordsDataPayload),
        nameof(User.EncryptedUserDevicesDataPayload),
        nameof(User.KeyEpoch),
        nameof(User.MembershipEpoch)
    ], userId: row => row.UId, targetedColumn: nameof(User.SavedKey));

    public async Task<IReadOnlyList<User>> ListAllAsync(CancellationToken ct = default) =>
        await session.ReadAsync(Select, ReadRow, ct);

    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // The canonical update and its checkpoint can both be verified before the flush.
        // Reuse the staged user so a subsequent update retains the original concurrency values.
        var pending = session.GetPendingRows<User>().LastOrDefault(item => item.Row.UId == id);
        return pending.Row is not null && pending.Operation == SqliteMutation.Operation.Update
            ? Task.FromResult<User?>(pending.Row)
            : GetAsync(id, remember: true, ct);
    }

    public Task<User?> GetByIdAsNoTrackingAsync(Guid id, CancellationToken ct = default) =>
        GetAsync(id, remember: false, ct);

    public async Task<(bool found, User? entity)> TryGetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var user = await GetByIdAsync(id, ct);
        return (user is not null, user);
    }

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct = default) =>
        session.ExistsAsync("""SELECT 1 FROM "Users" WHERE "UId" = $id""", ct, ("$id", id));

    public async Task<IReadOnlyList<Guid>> ListUserIdsAsync(CancellationToken ct = default) =>
        await session.ReadAsync("""SELECT "UId" FROM "Users";""", row => row.GetGuid(0), ct);

    public async Task<IReadOnlyList<User>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        await session.ReadByIdsAsync(ids, Select + """ WHERE u."UId" IN ({ids});""", ReadForWrite, ct);

    public Task<User?> GetByIdWithRelationsAsync(Guid id, CancellationToken ct = default) =>
        GetWithRelationsAsync(id, remember: true, ct);

    public Task<User?> GetByIdAsNoTrackingWithRelationsAsync(Guid id, CancellationToken ct = default) =>
        GetWithRelationsAsync(id, remember: false, ct);

    public Task AddAsync(User entity, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _rows.Stage(entity, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    public void Update(User entity) => _rows.Stage(entity, SqliteMutation.Operation.Update);

    public void Delete(User entity) => _rows.Stage(entity, SqliteMutation.Operation.Delete);

    public Task<IReadOnlyList<UserLoginLookupData>> ListLoginLookupDataAsync(CancellationToken ct = default) =>
        _login.ListLookupDataAsync(ct);

    public Task<IReadOnlyList<UserLoginIdentityState>> ListLoginIdentityStatesAsync(CancellationToken ct = default) =>
        _login.ListAsync(ct);

    public Task<UserLoginIdentityState?> GetLoginIdentityStateAsync(Guid userId, CancellationToken ct = default) =>
        _login.GetAsync(userId, ct);

    public Task AddLoginIdentityStateAsync(UserLoginIdentityState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _login.Stage(state, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    public void UpdateLoginIdentityState(UserLoginIdentityState state) =>
        _login.Stage(state, SqliteMutation.Operation.Update);

    public void DeleteLoginIdentityState(UserLoginIdentityState state) =>
        _login.Stage(state, SqliteMutation.Operation.Delete);

    public async Task UpdateSavedKeyAsync(Guid id, byte[]? savedKey, CancellationToken ct = default)
    {
        // This targeted write takes effect immediately and joins an active transaction.
        using var command = await session.CreateCommandAsync("""UPDATE "Users" SET "SavedKey" = $key WHERE "UId" = $id;""", ct);
        command.Parameters.Add("$key", SqliteType.Blob).Value = (object?)savedKey ?? DBNull.Value;
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<User>> GetAllRememberMeEnabledUsersAsync(CancellationToken ct = default) =>
        await session.ReadAsync($"""{Select} WHERE u."SavedKey" IS NOT NULL;""", ReadRow, ct);

    private async Task<User?> GetAsync(Guid id, bool remember, CancellationToken ct) =>
        (await session.ReadAsync<User>($"""{Select} WHERE u."UId" = $id;""",
            remember ? ReadForWrite : ReadRow, ct, ("$id", id))).SingleOrDefault();

    private Task<User?> GetWithRelationsAsync(Guid id, bool remember, CancellationToken ct) =>
        session.ReadSnapshotAsync<User?>(async () =>
        {
            var user = await GetAsync(id, remember, ct);
            if (user is null)
                return null;

            user.Groups = await session.ReadAsync($"""
                SELECT {SqliteGroupRepository.Projection}
                FROM "GroupMembers" gm JOIN "Groups" g ON g."Id" = gm."GroupsId"
                WHERE gm."UsersUId" = $id;
                """, SqliteGroupRepository.ReadRow, ct, ("$id", id));
            user.UserDevices = await session.ReadAsync($"""
                SELECT {SqliteUserDeviceRepository.Projection} FROM "UserDevices" ud WHERE ud."UserId" = $id;
                """, row => SqliteUserDeviceRepository.ReadRow(row, 0), ct, ("$id", id));
            user.LocalUserDevices = await session.ReadAsync($"""
                SELECT {SqliteLocalUserDeviceRepository.Projection} FROM "LocalUserDevices" lu WHERE lu."UserId" = $id;
                """, SqliteLocalUserDeviceRepository.ReadRow, ct, ("$id", id));

            foreach (var group in user.Groups)
                group.Users.Add(user);
            foreach (var link in user.UserDevices)
                link.User = user;
            foreach (var link in user.LocalUserDevices)
                link.User = user;
            return user;
        }, ct);

    private User ReadForWrite(SqliteDataReader row) => _rows.Remember(ReadRow(row));

    internal static User ReadRow(SqliteDataReader row) => new()
    {
        UId = row.GetGuid(0),
        EncryptedGeneralUserDataPayload = row.Blob(1),
        EncryptedPayload = row.Blob(2),
        EncryptedUserDevicesDataPayload = row.Blob(3),
        EncryptedUserPasswordsDataPayload = row.Blob(4),
        GeneralDataVersionLogicalCounter = row.GetInt64(5),
        GeneralDataVersionOriginDeviceId = row.GetGuid(6),
        GeneralDataVersionOriginInstanceId = row.GetGuid(7),
        GeneralDataVersionPhysicalTimeUnixMilliseconds = row.GetInt64(8),
        GeneralUserDataLastModifiedAt = row.UtcOffset(9),
        IntegrityHash = row.Blob(10),
        KeyEpoch = row.GetInt64(11),
        LastModifiedAt = row.UtcOffset(12),
        MembershipEpoch = row.GetInt64(13),
        PasswordSalt = row.Blob(14),
        SavedKey = row.IsDBNull(15) ? null : row.Blob(15),
        UserDataLastModifiedAt = row.UtcOffset(16),
        UserDevicesDataLastModifiedAt = row.UtcOffset(17),
        UserPasswordsDataLastModifiedAt = row.UtcOffset(18),
        UsernameHash = row.Blob(19),
        UsernameSalt = row.Blob(20)
    };
}
