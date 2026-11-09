using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Repositories;

internal sealed class UserDeviceRepository(SqliteSession session) : IUserDeviceRepository
{
    internal const string Projection = """
        ud."UserId", ud."DeviceId", ud."DeletedAt", ud."IntegrityHash",
        ud."IsDeleted", ud."IsSyncOn", ud."LastModifiedAt", ud."ModelId"
        """;
    private const int FieldCount = 8;
    private const string Select = $"""SELECT {Projection} FROM "UserDevices" ud""";
    private const string SelectWithDevice = $"""
        SELECT {Projection}, {DeviceRepository.Projection}
        FROM "UserDevices" ud JOIN "Devices" d ON d."Id" = ud."DeviceId"
        """;
    private const string LinkByPair = """ud."UserId" = $user AND ud."DeviceId" = $device""";
    private const string ActiveSyncLink = """ud."IsDeleted" = 0 AND ud."IsSyncOn" = 1""";

    private readonly SqliteRowWriter<UserDevice> _rows = new(session, "UserDevices", 2,
    [
        (nameof(UserDevice.UserId), row => row.UserId),
        (nameof(UserDevice.DeviceId), row => row.DeviceId),
        (nameof(UserDevice.DeletedAt), row => row.DeletedAt),
        (nameof(UserDevice.IntegrityHash), row => row.IntegrityHash),
        (nameof(UserDevice.IsDeleted), row => row.IsDeleted),
        (nameof(UserDevice.IsSyncOn), row => row.IsSyncOn),
        (nameof(UserDevice.LastModifiedAt), row => row.LastModifiedAt),
        (nameof(UserDevice.ModelId), row => row.ModelId)
    ]);

    public async Task<IReadOnlyList<UserDevice>> ListByUserAsync(Guid userId, CancellationToken ct = default) =>
        await session.ReadAsync($"""{Select} WHERE ud."UserId" = $user;""", ReadDetached, ct, ("$user", userId));

    public async Task<IReadOnlyList<UserDevice>> ListByUserWithDevicesAsync(Guid userId, CancellationToken ct = default) =>
        await session.ReadAsync($"""{SelectWithDevice} WHERE ud."UserId" = $user;""", ReadWithDevice, ct, ("$user", userId));

    public async Task<IReadOnlyList<UserDevice>> ListByUsersWithDevicesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        await session.ReadByIdsAsync(userIds, SelectWithDevice + """ WHERE ud."UserId" IN ({ids});""", ReadWithDevice, ct);

    public async Task<IReadOnlyList<UserDevice>> ListByDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        await session.ReadAsync($"""{Select} WHERE ud."DeviceId" = $device;""", ReadDetached, ct, ("$device", deviceId));

    public async Task<IReadOnlyList<UserDevice>> ListByUserIdsAndDeviceAsync(
        IReadOnlyCollection<Guid> userIds, Guid deviceId, CancellationToken ct = default) =>
        await session.ReadByIdsAsync(userIds,
            Select + """ WHERE ud."DeviceId" = $device AND ud."UserId" IN ({ids});""",
            row => _rows.Remember(ReadDetached(row)), ct, ("$device", deviceId));

    public async Task<UserDevice?> GetAsync(Guid userId, Guid deviceId, CancellationToken ct = default) =>
        (await session.ReadAsync($"""{Select} WHERE {LinkByPair};""",
            row => _rows.Remember(ReadDetached(row)), ct, ("$user", userId), ("$device", deviceId))).SingleOrDefault();

    public async Task<UserDevice?> GetWithDeviceAsync(Guid userId, Guid deviceId, CancellationToken ct = default) =>
        (await session.ReadAsync($"""{SelectWithDevice} WHERE {LinkByPair};""",
            row => _rows.Remember(ReadWithDevice(row)), ct, ("$user", userId), ("$device", deviceId))).SingleOrDefault();

    public async Task<UserDevice?> GetByModelIdAsync(Guid modelId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var pending = session.GetPendingRows<UserDevice>().LastOrDefault(item => item.Row.ModelId == modelId);
        if (pending.Row is not null && pending.Operation == SqliteMutation.Operation.Update)
        {
            pending.Row.VerifyIntegrity();
            return pending.Row;
        }

        return (await session.ReadAsync($"""{Select} WHERE ud."ModelId" = $model;""",
            row => _rows.Remember(ReadDetached(row)), ct, ("$model", modelId))).SingleOrDefault();
    }

    public Task<bool> ExistsAsync(Guid userId, Guid deviceId, CancellationToken ct = default) =>
        ExistsAsync(LinkByPair, ct, ("$user", userId), ("$device", deviceId));

    public Task<bool> HasActiveLinkAsync(Guid userId, Guid deviceId, CancellationToken ct = default) =>
        ExistsAsync($"{LinkByPair} AND {ActiveSyncLink}", ct, ("$user", userId), ("$device", deviceId));

    public async Task<bool> HasAnyActiveLinkAsync(IReadOnlyCollection<Guid> userIds, Guid deviceId, CancellationToken ct = default)
    {
        if (userIds.Count == 0 || deviceId == Guid.Empty)
            return false;

        var found = await session.ReadByIdsAsync(userIds, $$"""
            SELECT 1 FROM "UserDevices" ud
            WHERE ud."DeviceId" = $device AND {{ActiveSyncLink}} AND ud."UserId" IN ({ids})
            LIMIT 1;
            """, row => row.GetInt32(0), ct, ("$device", deviceId));
        return found.Count != 0;
    }

    public Task<bool> HasAnyActiveLinkForDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        ExistsAsync("""ud."DeviceId" = $device AND ud."IsDeleted" = 0""", ct, ("$device", deviceId));

    public Task<bool> HasAnyActiveSyncEnabledLinkForDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        ExistsAsync($"""ud."DeviceId" = $device AND {ActiveSyncLink}""", ct, ("$device", deviceId));

    public Task<bool> HasAnyDeletedLinkForDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        ExistsAsync("""ud."DeviceId" = $device AND ud."IsDeleted" = 1""", ct, ("$device", deviceId));

    public Task<bool> HasAnyActiveLinkForDeviceExceptUserAsync(Guid deviceId, Guid userId, CancellationToken ct = default) =>
        ExistsAsync("""ud."DeviceId" = $device AND ud."UserId" <> $user AND ud."IsDeleted" = 0""",
            ct, ("$device", deviceId), ("$user", userId));

    public Task<bool> SharesActiveUserAsync(Guid sourceDeviceId, Guid targetDeviceId, CancellationToken ct = default) =>
        ExistsAsync("""
            ud."DeviceId" = $target AND ud."IsDeleted" = 0 AND EXISTS (
                SELECT 1 FROM "UserDevices" source WHERE source."DeviceId" = $source
                  AND source."UserId" = ud."UserId" AND source."IsDeleted" = 0 AND source."IsSyncOn" = 1)
            """, ct, ("$source", sourceDeviceId), ("$target", targetDeviceId));

    public Task AddAsync(UserDevice userDevice, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        StageWrite(userDevice, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    public void Update(UserDevice userDevice) => StageWrite(userDevice, SqliteMutation.Operation.Update);

    public void Delete(UserDevice userDevice) => _rows.Stage(userDevice, SqliteMutation.Operation.Delete);

    private Task<bool> ExistsAsync(string predicate, CancellationToken ct, params (string Name, object? Value)[] parameters) =>
        session.ExistsAsync($"""SELECT 1 FROM "UserDevices" ud WHERE {predicate}""", ct, parameters);

    private void StageWrite(UserDevice link, SqliteMutation.Operation operation)
    {
        link.DeletedAt = UtcDateTimeUtil.ToUtc(link.DeletedAt);
        link.LastModifiedAt = UtcDateTimeUtil.ToUtc(link.LastModifiedAt);
        link.GenerateIntegrityHash();
        _rows.Stage(link, operation);
    }

    private static UserDevice ReadDetached(SqliteDataReader row) => ReadRow(row, 0);

    private static UserDevice ReadWithDevice(SqliteDataReader row)
    {
        var link = ReadDetached(row);
        link.Device = DeviceRepository.ReadRow(row, FieldCount);
        return link;
    }

    internal static UserDevice ReadRow(SqliteDataReader row, int offset)
    {
        var link = new UserDevice
        {
            UserId = row.GetGuid(offset),
            DeviceId = row.GetGuid(offset + 1),
            DeletedAt = row.NullableUtcOffset(offset + 2),
            IntegrityHash = row.Blob(offset + 3),
            IsDeleted = row.GetBoolean(offset + 4),
            IsSyncOn = row.GetBoolean(offset + 5),
            LastModifiedAt = row.UtcOffset(offset + 6),
            ModelId = row.GetGuid(offset + 7)
        };
        link.VerifyIntegrity();
        return link;
    }
}
