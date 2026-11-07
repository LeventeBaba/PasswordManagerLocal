using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Projections;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Contracts.Notifications;

namespace PasswordManagerLocal.Common.Backend.Repositories.Sqlite;

internal sealed class SqliteGroupRepository(SqliteSession session) : IGroupRepository
{
    internal const string Projection = """g."Id", g."EncryptedPayload", g."IntegrityHash", g."LastModifiedAt" """;
    private const string Select = $"""SELECT {Projection} FROM "Groups" g""";
    private readonly Dictionary<(Guid GroupId, Guid UserId), MembershipRow> _membershipRows = [];

    private readonly SqliteRowWriter<Group> _rows = new(session, "Groups", 1,
    [
        (nameof(Group.Id), row => row.Id),
        (nameof(Group.EncryptedPayload), row => row.EncryptedPayload),
        (nameof(Group.IntegrityHash), row => row.IntegrityHash),
        (nameof(Group.LastModifiedAt), row => row.LastModifiedAt)
    ]);

    public async Task<IReadOnlyList<Group>> ListAllAsync(CancellationToken ct = default) =>
        await session.ReadAsync(Select, ReadRow, ct);

    public async Task<Group?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        (await session.ReadAsync($"""{Select} WHERE g."Id" = $id;""",
            row => _rows.Remember(ReadRow(row)), ct, ("$id", id))).SingleOrDefault();

    public async Task<(bool found, Group? entity)> TryGetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var group = await GetByIdAsync(id, ct);
        return (group is not null, group);
    }

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct = default) =>
        session.ExistsAsync("""SELECT 1 FROM "Groups" WHERE "Id" = $id""", ct, ("$id", id));

    public Task AddAsync(Group entity, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _rows.Stage(entity, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    public void Update(Group entity) => _rows.Stage(entity, SqliteMutation.Operation.Update);

    public void Delete(Group entity)
    {
        if (session.GetPendingOperation(entity) == SqliteMutation.Operation.Insert)
        {
            foreach (var (key, member) in _membershipRows)
            {
                if (key.GroupId == entity.Id)
                    session.DiscardPendingRow(member);
            }
        }
        _rows.Stage(entity, SqliteMutation.Operation.Delete);
    }

    public async Task<IReadOnlyList<Group>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        await session.ReadByIdsAsync(ids, Select + """ WHERE g."Id" IN ({ids});""",
            row => _rows.Remember(ReadRow(row)), ct);

    public Task<Group?> GetByIdWithUsersAsync(Guid id, CancellationToken ct = default) =>
        session.ReadSnapshotAsync<Group?>(async () =>
        {
            var group = await GetByIdAsync(id, ct);
            if (group is null)
                return null;

            group.Users = await session.ReadAsync($"""
                SELECT {SqliteUserRepository.Projection}
                FROM "GroupMembers" gm JOIN "Users" u ON u."UId" = gm."UsersUId"
                WHERE gm."GroupsId" = $id;
                """, SqliteUserRepository.ReadRow, ct, ("$id", id));
            foreach (var user in group.Users)
                user.Groups.Add(group);
            return group;
        }, ct);

    public Task<GroupWithUserIdsData?> GetWithUserIdsAsNoTrackingAsync(Guid id, CancellationToken ct = default) =>
        session.ReadSnapshotAsync<GroupWithUserIdsData?>(async () =>
        {
            var groups = await session.ReadAsync($"""{Select} WHERE g."Id" = $id;""", ReadRow, ct, ("$id", id));
            var group = groups.SingleOrDefault();
            if (group is null)
                return null;

            return Project(group, await ListUserIdsAsync(id, ct));
        }, ct);

    public Task<IReadOnlyList<GroupWithUserIdsData>> ListByUserWithUserIdsAsNoTrackingAsync(
        Guid userId, CancellationToken ct = default) =>
        session.ReadSnapshotAsync<IReadOnlyList<GroupWithUserIdsData>>(async () =>
        {
            var groups = await session.ReadAsync($"""
                {Select} JOIN "GroupMembers" gm ON gm."GroupsId" = g."Id"
                WHERE gm."UsersUId" = $user;
                """, ReadRow, ct, ("$user", userId));
            var members = await session.ReadByIdsAsync(groups.Select(group => group.Id).ToArray(), """
                SELECT gm."GroupsId", u."UId" FROM "GroupMembers" gm JOIN "Users" u ON u."UId" = gm."UsersUId"
                WHERE gm."GroupsId" IN ({ids});
                """, row => (GroupId: row.GetGuid(0), UserId: row.GetGuid(1)), ct);
            var byGroup = members.ToLookup(member => member.GroupId, member => member.UserId);
            return groups.Select(group => Project(group, byGroup[group.Id])).ToList();
        }, ct);

    public async Task<IReadOnlyList<Guid>> ListUserIdsAsync(Guid groupId, CancellationToken ct = default) =>
        await session.ReadAsync("""
            SELECT DISTINCT u."UId" FROM "GroupMembers" gm JOIN "Users" u ON u."UId" = gm."UsersUId"
            JOIN "Groups" g ON g."Id" = gm."GroupsId" WHERE gm."GroupsId" = $id;
            """, row => row.GetGuid(0), ct, ("$id", groupId));

    public async Task<IReadOnlyList<Guid>> ListIdsByUserAsync(Guid userId, CancellationToken ct = default) =>
        await session.ReadAsync("""
            SELECT g."Id" FROM "GroupMembers" gm JOIN "Groups" g ON g."Id" = gm."GroupsId"
            WHERE gm."UsersUId" = $id;
            """, row => row.GetGuid(0), ct, ("$id", userId));

    public void SetMember(Group group, User user, bool isMember)
    {
        var key = (group.Id, user.UId);
        if (!_membershipRows.TryGetValue(key, out var row))
            _membershipRows.Add(key, row = new MembershipRow());

        var operation = isMember ? SqliteMutation.Operation.Insert : SqliteMutation.Operation.Delete;
        var pending = session.GetPendingOperation(row);
        if (pending is not null && pending != operation)
        {
            session.DiscardPendingRow(row);
        }
        else
        {
            var sql = isMember
                ? """INSERT INTO "GroupMembers" ("GroupsId", "UsersUId") VALUES ($group, $user);"""
                : """DELETE FROM "GroupMembers" WHERE "GroupsId" = $group AND "UsersUId" = $user;""";
            var changes = new BackendChangeSet();
            changes.Add(user.UId, FrontendChangeScope.Passwords);
            session.StageRow(new SqliteMutation(typeof(MembershipRow), operation, user.UId, changes,
                sql, [("$group", group.Id), ("$user", user.UId)]) { Row = row });
        }

        group.SetMember(user, isMember);
        if (session.GetPendingOperation(group) == SqliteMutation.Operation.Insert)
            _rows.Stage(group, SqliteMutation.Operation.Insert);
    }

    internal static Group ReadRow(SqliteDataReader row) => new()
    {
        Id = row.GetGuid(0),
        EncryptedPayload = row.Blob(1),
        IntegrityHash = row.Blob(2),
        LastModifiedAt = row.UtcOffset(3)
    };

    private static GroupWithUserIdsData Project(Group group, IEnumerable<Guid> userIds) => new()
    {
        Id = group.Id,
        EncryptedPayload = group.EncryptedPayload,
        IntegrityHash = group.IntegrityHash,
        LastModifiedAt = group.LastModifiedAt,
        UserIds = userIds.ToList()
    };

    private sealed class MembershipRow { }
}
