using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories.Sqlite;

internal sealed class SqliteSyncItemRepository(SqliteSession session) : ISyncItemRepository
{
    internal const string Projection = """s."Id", s."ModelId", s."ModelType", s."ChangeType", s."ChangedAtTs" """;
    private const string Select = $"""SELECT {Projection} FROM "SyncItems" s""";

    private readonly SqliteRowWriter<SyncItem> _rows = new(session, "SyncItems", 1,
    [
        (nameof(SyncItem.Id), row => row.Id),
        (nameof(SyncItem.ModelId), row => row.ModelId),
        (nameof(SyncItem.ModelType), row => (byte)row.ModelType),
        (nameof(SyncItem.ChangeType), row => (byte)row.ChangeType),
        (nameof(SyncItem.ChangedAtTs), row => row.ChangedAtTs)
    ]);

    public async Task<IReadOnlyList<SyncItem>> ListAllAsync(CancellationToken ct = default) =>
        await session.ReadAsync(Select, row => ReadRow(row, 0), ct);

    public async Task<SyncItem?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        (await session.ReadAsync($"""{Select} WHERE s."Id" = $id;""",
            row => _rows.Remember(ReadRow(row, 0)), ct, ("$id", id))).SingleOrDefault();

    public async Task<(bool found, SyncItem? entity)> TryGetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var row = await GetByIdAsync(id, ct);
        return (row is not null, row);
    }

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct = default) =>
        session.ExistsAsync("""SELECT 1 FROM "SyncItems" WHERE "Id" = $id""", ct, ("$id", id));

    public async Task<SyncItem?> GetAsync(Guid modelId, SyncModelType modelType, CancellationToken ct = default) =>
        (await session.ReadAsync($"""{Select} WHERE s."ModelId" = $id AND s."ModelType" = $type;""",
            row => _rows.Remember(ReadRow(row, 0)), ct, ("$id", modelId), ("$type", (byte)modelType))).SingleOrDefault();

    public Task AddAsync(SyncItem entity, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _rows.Stage(entity, SqliteMutation.Operation.Insert);
        return Task.CompletedTask;
    }

    public void Update(SyncItem entity) => _rows.Stage(entity, SqliteMutation.Operation.Update);

    public void Delete(SyncItem entity) => _rows.Stage(entity, SqliteMutation.Operation.Delete);

    public async Task<int> ClearSyncItemsAsync(CancellationToken ct = default)
    {
        using var command = await session.CreateCommandAsync("""
            DELETE FROM "SyncItems"
            WHERE NOT EXISTS (SELECT 1 FROM "SyncQueueItems" q WHERE q."SyncItemId" = "SyncItems"."Id");
            """, ct);
        return await command.ExecuteNonQueryAsync(ct);
    }

    internal static SyncItem ReadRow(SqliteDataReader row, int offset) => new()
    {
        Id = row.GetGuid(offset),
        ModelId = row.GetGuid(offset + 1),
        ModelType = (SyncModelType)row.GetByte(offset + 2),
        ChangeType = (SyncChangeType)row.GetByte(offset + 3),
        ChangedAtTs = row.GetInt64(offset + 4)
    };
}
