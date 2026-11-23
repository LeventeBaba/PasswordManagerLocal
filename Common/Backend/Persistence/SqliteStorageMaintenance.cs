using System.Globalization;
using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Contracts.Errors;

namespace PasswordManagerLocal.Common.Backend.Persistence;

internal static class SqliteStorageMaintenance
{
    // Run for every opened connection, including pooled handles and the independent version clock.
    public static void ConfigureConnection(SqliteConnection connection,
        long maximumDatabaseBytes = StorageCapacityConstants.MainDatabaseBytes)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA page_size;";
        var pageSize = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (pageSize <= 0) throw new InvalidDataException("SQLite did not return a valid page size.");
        var maximumPages = maximumDatabaseBytes / pageSize;
        if (maximumPages <= 0) throw new StorageCapacityExceededException();
        command.CommandText = "PRAGMA max_page_count = " + maximumPages.ToString(CultureInfo.InvariantCulture) + ";";
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > maximumPages)
            throw new StorageCapacityExceededException();
        command.CommandText = $"""
            PRAGMA recursive_triggers = ON;
            PRAGMA mmap_size = 0;
            PRAGMA temp_store = FILE;
            PRAGMA wal_autocheckpoint = {StorageCapacityConstants.WalAutoCheckpointPages};
            PRAGMA journal_size_limit = {StorageCapacityConstants.RetainedJournalBytes};
            """;
        command.ExecuteNonQuery();
    }

    // Called only before creating the first application table. Existing images retain their
    // format; converting them would require a full rewrite and separate verified promotion.
    public static void ConfigureNewDatabase(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA auto_vacuum = INCREMENTAL;";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA auto_vacuum;";
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 2)
            throw new InvalidOperationException("Incremental database compaction could not be configured.");
    }

    // SQLite moves only live pages and retires only freelist pages. No table/evidence deletion.
    // One bounded batch at startup avoids a full-vault rewrite or a background polling loop.
    public static async Task TryReclaimFreePagesAsync(SqliteConnection connection, CancellationToken ct)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA auto_vacuum;";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != 2) return;
            command.CommandText = $"PRAGMA incremental_vacuum({StorageCapacityConstants.IncrementalVacuumPages});";
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6 || SqliteCapacityError.IsCapacityFailure(exception))
        {
            // A pinned reader, exhausted WAL or a competing writer postpones reclamation.
            // Successful application writes remain committed and no sidecar is manually removed.
        }
    }
}
