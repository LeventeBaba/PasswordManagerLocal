using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Sync.Tombstones;

namespace PasswordManagerLocal.Common.Backend.Repositories;

internal sealed class SyncTombstoneRepository(SqliteSession session, int capacity = TombstoneConstants.MaxSyncTombstones)
    : ISyncTombstoneRepository
{
    private readonly Dictionary<(Guid ModelId, SyncModelType ModelType), PendingTombstone> _pending = [];

    public Task<SyncTombstone?> GetAsync(Guid modelId, SyncModelType modelType, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_pending.TryGetValue((modelId, modelType), out var entry) &&
            session.GetPendingOperation(entry) is { } operation)
        {
            return Task.FromResult(operation == SqliteMutation.Operation.Delete ? null : entry.Value);
        }

        return GetPersistedAsync(modelId, modelType, ct);
    }

    public async Task UpsertAsync(Guid modelId, SyncModelType modelType, long deletedAtTs, CancellationToken ct = default)
    {
        var entry = GetPendingEntry(modelId, modelType);
        var operation = session.GetPendingOperation(entry);
        var previous = operation == SqliteMutation.Operation.Insert
            ? entry.Value
            : await GetPersistedAsync(modelId, modelType, ct);
        if (previous is not null && previous.DeletedAtTs >= deletedAtTs && operation != SqliteMutation.Operation.Delete)
            return;

        if (previous is null)
        {
            using var count = await session.CreateCommandAsync("""SELECT COUNT(*) FROM "SyncTombstones";""", ct);
            var persisted = checked((int)(long)(await count.ExecuteScalarAsync(ct))!);
            var pending = _pending.Values.Count(row =>
                row.IsNew && session.GetPendingOperation(row) == SqliteMutation.Operation.Insert);
            SyncTombstonePolicy.EnsureCapacity(capacity, persisted, pending);
        }

        // A second connection can add rows between the early check and SaveChanges.
        // Recheck capacity in the insert itself, inside the session's writer transaction.
        var value = new SyncTombstone
        {
            Id = previous?.Id ?? Guid.NewGuid(),
            ModelId = modelId,
            ModelType = modelType,
            DeletedAtTs = previous is null ? deletedAtTs : Math.Max(previous.DeletedAtTs, deletedAtTs)
        };
        session.StageRow(new SqliteMutation(typeof(SyncTombstone), SqliteMutation.Operation.Insert, null, null, """
            INSERT INTO "SyncTombstones" ("Id", "ModelId", "ModelType", "DeletedAtTs")
            SELECT $id, $model, $type, $deleted
            WHERE EXISTS (SELECT 1 FROM "SyncTombstones" WHERE "ModelId" = $model AND "ModelType" = $type)
               OR (SELECT COUNT(*) FROM "SyncTombstones") < $capacity
            ON CONFLICT ("ModelId", "ModelType") DO UPDATE
            SET "DeletedAtTs" = MAX("SyncTombstones"."DeletedAtTs", excluded."DeletedAtTs");
            """, [("$id", value.Id), ("$model", modelId), ("$type", (byte)modelType), ("$deleted", value.DeletedAtTs),
                ("$capacity", capacity)])
        {
            Row = entry,
            NoRowsFailure = () => SyncTombstonePolicy.CapacityExceeded(capacity)
        });
        entry.IsNew = previous is null || (operation == SqliteMutation.Operation.Insert && entry.IsNew);
        entry.Value = value;
    }

    public void Delete(SyncTombstone tombstone)
    {
        var entry = GetPendingEntry(tombstone.ModelId, tombstone.ModelType);
        if (entry.IsNew && session.GetPendingOperation(entry) == SqliteMutation.Operation.Insert)
        {
            session.DiscardPendingRow(entry);
            return;
        }

        session.StageRow(new SqliteMutation(typeof(SyncTombstone), SqliteMutation.Operation.Delete, null, null, """
            DELETE FROM "SyncTombstones" WHERE "Id" = $id AND "DeletedAtTs" <= $observed;
            """, [("$id", tombstone.Id), ("$observed", tombstone.DeletedAtTs)]) { Row = entry });
    }

    private PendingTombstone GetPendingEntry(Guid modelId, SyncModelType modelType)
    {
        var key = (modelId, modelType);
        if (!_pending.TryGetValue(key, out var entry))
            _pending.Add(key, entry = new PendingTombstone());
        return entry;
    }

    private async Task<SyncTombstone?> GetPersistedAsync(Guid modelId, SyncModelType modelType, CancellationToken ct) =>
        (await session.ReadAsync("""
            SELECT "Id", "ModelId", "ModelType", "DeletedAtTs"
            FROM "SyncTombstones" WHERE "ModelId" = $model AND "ModelType" = $type;
            """, ReadRow, ct, ("$model", modelId), ("$type", (byte)modelType))).SingleOrDefault();

    private static SyncTombstone ReadRow(SqliteDataReader row) => new()
    {
        Id = row.GetGuid(0),
        ModelId = row.GetGuid(1),
        ModelType = (SyncModelType)row.GetByte(2),
        DeletedAtTs = row.GetInt64(3)
    };

    private sealed class PendingTombstone
    {
        public SyncTombstone? Value { get; set; }
        public bool IsNew { get; set; }
    }
}
