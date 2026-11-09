using Microsoft.Data.Sqlite;
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
            await transaction.CommitAsync(ct);
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync();
        }
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
