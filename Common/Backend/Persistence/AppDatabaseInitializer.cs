using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Models;
using System.Data;

namespace PasswordManagerLocal.Common.Backend.Persistence;

internal static class AppDatabaseInitializer
{
    private const string InitialSchemaResource = "PasswordManagerLocal.Common.Backend.Persistence.Schema.InitialSchema.sql";

    private const string RecoveryJournalSchema = """
        CREATE TABLE IF NOT EXISTS "UserRecoveryJournals" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_UserRecoveryJournals" PRIMARY KEY,
            "UserId" TEXT NOT NULL,
            "State" INTEGER NOT NULL,
            "CreatedAtUtc" TEXT NOT NULL,
            "ProtectedKey" BLOB NOT NULL,
            "EncryptedPayload" BLOB NOT NULL,
            "PayloadHash" BLOB NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_UserRecoveryJournals_UserId" ON "UserRecoveryJournals" ("UserId");
        """;

    // Model type 2 was persisted for the retired pre-release group model.
    private static readonly string RetiredGroupSchemaCleanup = $"""
        DELETE FROM "SyncQueueItems"
        WHERE "SyncItemId" IN (SELECT "Id" FROM "SyncItems" WHERE "ModelType" = 2);
        DELETE FROM "SyncItems" WHERE "ModelType" = 2;
        DELETE FROM "SyncTombstones" WHERE "ModelType" = 2;
        DROP TABLE IF EXISTS "GroupMembers";
        DROP TABLE IF EXISTS "Groups";

        UPDATE "UserSyncSnapshots"
        SET "Status" = {(byte)UserSyncSnapshotStatus.SupersededBadEvidence}
        WHERE "Status" <> {(byte)UserSyncSnapshotStatus.IsolatedFork}
          AND CASE WHEN json_valid(CAST("EnvelopePayload" AS TEXT))
                   THEN json_array_length(CAST("EnvelopePayload" AS TEXT), '$.User.GroupIds') > 0
              END;
        """;

    public static async Task InitializeAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync(ct);

        try
        {
            // SQLite cannot switch to WAL inside a transaction. Existing vaults keep
            // their configured journal mode.
            if (!await HasTablesAsync(connection, transaction: null, ct))
                await ExecuteAsync(connection, transaction: null, "PRAGMA journal_mode = WAL;", ct);

            await using var transaction = connection.BeginTransaction(deferred: false);
            if (!await HasTablesAsync(connection, transaction, ct))
            {
                using var reader = new StreamReader(
                    typeof(AppDatabaseInitializer).Assembly.GetManifestResourceStream(InitialSchemaResource)
                    ?? throw new InvalidOperationException("The initial SQLite vault schema resource is missing."));
                await ExecuteAsync(connection, transaction, await reader.ReadToEndAsync(ct), ct);
            }

            await RunQuickCheckAsync(connection, transaction, ct);
            // Existing vaults receive this additive upgrade after passing quick_check.
            await ExecuteAsync(connection, transaction, RecoveryJournalSchema, ct);
            await RemoveRetiredGroupSchemaAsync(connection, transaction, ct);
            await transaction.CommitAsync(ct);
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync();
        }
    }

    private static async Task RemoveRetiredGroupSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM sqlite_schema
                           WHERE type = 'table' AND name IN ('Groups', 'GroupMembers'));
            """;
        if ((long)(await command.ExecuteScalarAsync(ct))! == 0)
            return;

        // Keep incompatible signed envelopes as historical evidence without relaying them.
        // Retain terminal forks so removing the old format cannot clear a quarantined origin.
        await ExecuteAsync(connection, transaction, RetiredGroupSchemaCleanup, ct);
    }

    private static async Task<bool> HasTablesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM sqlite_schema WHERE type = 'table' AND rootpage IS NOT NULL);";
        return (long)(await command.ExecuteScalarAsync(ct))! != 0;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task RunQuickCheckAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA quick_check;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(ct))
            rows.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));

        if (rows.Count != 1 || !string.Equals(rows[0], "ok", StringComparison.OrdinalIgnoreCase))
        {
            var diagnostic = rows.Count == 0 ? "no-result" : string.Join(";", rows.Take(8));
            throw new DatabaseUnreadableException(new InvalidDataException(
                $"Database structural integrity verification failed ({diagnostic}). Normal writes are disabled."));
        }
    }
}
