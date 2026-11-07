using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Repositories.Sqlite;

internal sealed class SqliteSyncQueueRepository(SqliteSession session) : ISyncQueueRepository
{
    private const string PendingForDevice = $"""
        SELECT q."QueueId", q."Id", q."DeviceId", q."SyncItemId", q."EnqueuedAt", q."ProcessedAt", {SqliteSyncItemRepository.Projection}
        FROM "SyncQueueItems" q JOIN "SyncItems" s ON s."Id" = q."SyncItemId"
        WHERE q."DeviceId" = $device AND q."ProcessedAt" IS NULL
        ORDER BY q."QueueId"
        """;

    private readonly Dictionary<(Guid SyncItemId, Guid DeviceId), PendingEnqueue> _pending = [];
    private readonly SqliteRowWriter<SyncQueueItem> _rows = new(session, "SyncQueueItems", 1,
    [
        (nameof(SyncQueueItem.QueueId), row => row.QueueId),
        (nameof(SyncQueueItem.Id), row => row.Id),
        (nameof(SyncQueueItem.DeviceId), row => row.DeviceId),
        (nameof(SyncQueueItem.SyncItemId), row => row.SyncItemId),
        (nameof(SyncQueueItem.EnqueuedAt), row => row.EnqueuedAt),
        (nameof(SyncQueueItem.ProcessedAt), row => row.ProcessedAt)
    ]);

    public Task<bool> ExistsAsync(Guid syncItemId, Guid deviceId, CancellationToken ct = default) =>
        session.ExistsAsync("""
            SELECT 1 FROM "SyncQueueItems" WHERE "SyncItemId" = $item AND "DeviceId" = $device AND "ProcessedAt" IS NULL
            """, ct, ("$item", syncItemId), ("$device", deviceId));

    public Task<bool> HasPendingForDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        session.ExistsAsync("""SELECT 1 FROM "SyncQueueItems" WHERE "DeviceId" = $device AND "ProcessedAt" IS NULL""",
            ct, ("$device", deviceId));

    public Task<bool> HasPendingForSyncItemAsync(Guid syncItemId, CancellationToken ct = default) =>
        session.ExistsAsync("""SELECT 1 FROM "SyncQueueItems" WHERE "SyncItemId" = $item AND "ProcessedAt" IS NULL""",
            ct, ("$item", syncItemId));

    public Task<bool> HasPendingForModelAsync(Guid modelId, SyncModelType modelType, CancellationToken ct = default) =>
        session.ExistsAsync("""
            SELECT 1 FROM "SyncQueueItems" q JOIN "SyncItems" s ON s."Id" = q."SyncItemId"
            WHERE s."ModelId" = $id AND s."ModelType" = $type AND q."ProcessedAt" IS NULL
            """, ct, ("$id", modelId), ("$type", (byte)modelType));

    public async Task<SyncQueueItem?> GetNextPendingForDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        (await GetNextPendingBatchForDeviceAsync(deviceId, 1, ct)).SingleOrDefault();

    public async Task<IReadOnlyList<SyncQueueItem>> GetNextPendingBatchForDeviceAsync(
        Guid deviceId, int limit, CancellationToken ct = default)
    {
        if (limit <= 0)
            return [];

        return await session.ReadAsync(PendingForDevice + " LIMIT $limit;", ReadForWrite, ct,
            ("$device", deviceId), ("$limit", limit));
    }

    public async Task<IReadOnlyList<SyncQueueItem>> ListPendingForDeviceWithItemsAsync(
        Guid deviceId, CancellationToken ct = default) =>
        await session.ReadAsync(PendingForDevice, ReadForWrite, ct, ("$device", deviceId));

    public async Task<IReadOnlyList<Guid>> ListQueuedDeviceIdsAsync(
        Guid syncItemId, IReadOnlyList<Guid> deviceIds, CancellationToken ct = default) =>
        await session.ReadByIdsAsync(deviceIds, """
            SELECT "DeviceId" FROM "SyncQueueItems"
            WHERE "SyncItemId" = $item AND "ProcessedAt" IS NULL AND "DeviceId" IN ({ids});
            """, row => row.GetGuid(0), ct, ("$item", syncItemId));

    public Task EnqueueAsync(Guid syncItemId, Guid deviceId, CancellationToken ct = default) =>
        EnqueueAsync([new SyncQueueItem
        {
            SyncItemId = syncItemId,
            DeviceId = deviceId,
            EnqueuedAt = SyncQueueGeneration.Next()
        }], ct);

    public async Task<bool> TryEnqueueAsync(Guid syncItemId, Guid deviceId, CancellationToken ct = default)
    {
        try
        {
            await EnqueueAsync(syncItemId, deviceId, ct);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public Task EnqueueAsync(IEnumerable<SyncQueueItem> items, CancellationToken ct = default)
    {
        foreach (var item in items.DistinctBy(item => (item.SyncItemId, item.DeviceId)))
        {
            ct.ThrowIfCancellationRequested();
            var pair = (item.SyncItemId, item.DeviceId);
            if (!_pending.TryGetValue(pair, out var row))
                _pending.Add(pair, row = new PendingEnqueue());
            if (session.GetPendingOperation(row) is null)
                row.Id = item.Id;

            session.StageRow(new SqliteMutation(typeof(SyncQueueItem), SqliteMutation.Operation.Insert, null, null, """
                INSERT INTO "SyncQueueItems" ("Id", "SyncItemId", "DeviceId", "EnqueuedAt", "ProcessedAt")
                VALUES ($id, $item, $device, $at, NULL)
                ON CONFLICT ("DeviceId", "SyncItemId") DO UPDATE
                SET "EnqueuedAt" = excluded."EnqueuedAt", "ProcessedAt" = NULL;
                """, [("$id", row.Id), ("$item", pair.SyncItemId), ("$device", pair.DeviceId), ("$at", item.EnqueuedAt)])
            {
                Row = row,
                PrepareAsync = BindGenerationAsync
            });
        }
        return Task.CompletedTask;
    }

    public async Task<bool> TryAcknowledgeAsync(Guid queueItemId, DateTimeOffset expectedEnqueuedAt,
        DateTimeOffset processedAt, CancellationToken ct = default)
    {
        using var command = await session.CreateCommandAsync("""
            UPDATE "SyncQueueItems" SET "ProcessedAt" = $processed
            WHERE "Id" = $id AND "ProcessedAt" IS NULL AND "EnqueuedAt" = $expected;
            """, ct);
        command.Parameters.AddWithValue("$processed", UtcDateTimeUtil.ToUtc(processedAt));
        command.Parameters.AddWithValue("$id", queueItemId);
        command.Parameters.AddWithValue("$expected", UtcDateTimeUtil.ToUtc(expectedEnqueuedAt));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public void Update(SyncQueueItem item) => _rows.Stage(item, SqliteMutation.Operation.Update);

    public void Delete(SyncQueueItem item) => _rows.Stage(item, SqliteMutation.Operation.Delete);

    private SyncQueueItem ReadForWrite(SqliteDataReader row)
    {
        var syncItem = SqliteSyncItemRepository.ReadRow(row, 6);
        var item = new SyncQueueItem
        {
            QueueId = row.GetInt64(0),
            Id = row.GetGuid(1),
            DeviceId = row.GetGuid(2),
            SyncItemId = row.GetGuid(3),
            EnqueuedAt = row.UtcOffset(4),
            ProcessedAt = row.NullableUtcOffset(5),
            SyncItem = syncItem
        };
        syncItem.QueueItems.Add(item);
        return _rows.Remember(item);
    }

    private static async Task BindGenerationAsync(SqliteCommand write, CancellationToken ct)
    {
        // The writer transaction serializes the indexed read and the pair upsert.
        var connection = (SqliteConnection)write.Connection!;
        using var read = connection.CreateCommand();
        read.Transaction = write.Transaction;
        read.CommandText = """
            SELECT "EnqueuedAt" FROM "SyncQueueItems" WHERE "DeviceId" = $device AND "SyncItemId" = $item;
            """;
        read.Parameters.AddWithValue("$device", write.Parameters["$device"].Value!);
        read.Parameters.AddWithValue("$item", write.Parameters["$item"].Value!);
        using var rows = await read.ExecuteReaderAsync(ct);
        var previous = await rows.ReadAsync(ct) ? rows.UtcOffset(0) : (DateTimeOffset?)null;
        var proposed = (DateTimeOffset)write.Parameters["$at"].Value!;
        write.Parameters["$at"].Value = SyncQueueGeneration.Next(previous, proposed);
    }

    private sealed class PendingEnqueue
    {
        public Guid Id { get; set; }
    }
}
