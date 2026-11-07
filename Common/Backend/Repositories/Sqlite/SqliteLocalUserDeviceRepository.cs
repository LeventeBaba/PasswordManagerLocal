using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories.Sqlite;

internal sealed class SqliteLocalUserDeviceRepository(SqliteSession session) : ILocalUserDeviceRepository
{
    internal const string Projection = """
        lu."UserId", lu."LocalDeviceIdentityId", lu."IntegrityHash", lu."IsSyncOn"
        """;
    private const int FieldCount = 4;

    private readonly SqliteRowWriter<LocalUserDevice> _rows = new(session, "LocalUserDevices", 2,
    [
        (nameof(LocalUserDevice.UserId), row => row.UserId),
        (nameof(LocalUserDevice.LocalDeviceIdentityId), row => row.LocalDeviceIdentityId),
        (nameof(LocalUserDevice.IntegrityHash), row => row.IntegrityHash),
        (nameof(LocalUserDevice.IsSyncOn), row => row.IsSyncOn)
    ]);

    public async Task<LocalUserDevice?> GetAsync(Guid userId, CancellationToken ct = default) =>
        (await session.ReadAsync($"""
            SELECT {Projection}, {SqliteDeviceIdentityRepository.Projection}
            FROM "LocalUserDevices" lu JOIN "LocalDeviceIdentity" li ON li."Id" = lu."LocalDeviceIdentityId"
            WHERE lu."UserId" = $user;
            """, row => _rows.Remember(ReadWithIdentity(row)), ct, ("$user", userId))).SingleOrDefault();

    public Task<bool> IsSyncOnAsync(Guid userId, CancellationToken ct = default) =>
        session.ExistsAsync("""SELECT 1 FROM "LocalUserDevices" WHERE "UserId" = $user AND "IsSyncOn" = 1""",
            ct, ("$user", userId));

    public Task<bool> AnySyncOnAsync(CancellationToken ct = default) =>
        session.ExistsAsync("""SELECT 1 FROM "LocalUserDevices" WHERE "IsSyncOn" = 1""", ct);

    public async Task<IReadOnlyList<Guid>> ListSyncOnUserIdsAsync(CancellationToken ct = default) =>
        await session.ReadAsync("""SELECT DISTINCT "UserId" FROM "LocalUserDevices" WHERE "IsSyncOn" = 1;""",
            row => row.GetGuid(0), ct);

    public Task AddAsync(LocalUserDevice localUserDevice, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        StageWrite(localUserDevice, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    public void Update(LocalUserDevice localUserDevice) => StageWrite(localUserDevice, SqliteMutation.Operation.Update);

    public void Delete(LocalUserDevice localUserDevice) => _rows.Stage(localUserDevice, SqliteMutation.Operation.Delete);

    private void StageWrite(LocalUserDevice link, SqliteMutation.Operation operation)
    {
        link.GenerateIntegrityHash();
        _rows.Stage(link, operation);
    }

    private static LocalUserDevice ReadWithIdentity(SqliteDataReader row)
    {
        var link = ReadRow(row);
        link.LocalDeviceIdentity = SqliteDeviceIdentityRepository.ReadRow(row, FieldCount);
        return link;
    }

    internal static LocalUserDevice ReadRow(SqliteDataReader row)
    {
        var link = new LocalUserDevice
        {
            UserId = row.GetGuid(0),
            LocalDeviceIdentityId = row.GetGuid(1),
            IntegrityHash = row.Blob(2),
            IsSyncOn = row.GetBoolean(3)
        };
        link.VerifyIntegrity();
        return link;
    }
}
