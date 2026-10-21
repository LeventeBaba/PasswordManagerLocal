using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Configuration;

namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>
/// Moves existing SQLCipher databases to SQLite3MC's default ChaCha20 cipher before EF Core
/// creates any pooled connections. The original database is left untouched until a staged copy
/// has been re-keyed and verified with the target cipher.
/// </summary>
internal static class DatabaseEncryptionMigrator
{
    private const string TargetCipher = "chacha20";
    private const int SqliteNotADatabaseErrorCode = 26;
    private static readonly int[] SqlCipherLegacyVersions = [4, 3, 2, 1];

    internal static string CreateRuntimeConnectionString(string databasePath, string password) =>
        CreateConnectionString(
            databasePath,
            password,
            $"cipher={TargetCipher}",
            SqliteOpenMode.ReadWriteCreate,
            pooling: true);

    internal static async Task<bool> MigrateIfRequiredAsync(
        BackendStoragePaths paths,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var databasePath = paths.DatabasePath;
        var temporaryPath = paths.DatabaseMigrationTempPath;
        var backupPath = paths.DatabaseMigrationBackupPath;

        SqliteConnection.ClearAllPools();
        RecoverMissingDatabaseFromBackup(databasePath, backupPath);

        if (!File.Exists(databasePath))
        {
            DeleteDatabaseArtifacts(temporaryPath, backupPath);
            return false;
        }

        if (await IsReadableAsync(databasePath, password, $"cipher={TargetCipher}", cancellationToken))
        {
            DeleteDatabaseArtifacts(temporaryPath, backupPath);
            return false;
        }

        var legacyVersion = await DetectSqlCipherLegacyVersionAsync(
            databasePath,
            password,
            cancellationToken);

        if (legacyVersion is null && File.Exists(backupPath))
        {
            legacyVersion = await DetectSqlCipherLegacyVersionAsync(
                backupPath,
                password,
                cancellationToken);
            if (legacyVersion is not null)
            {
                File.Move(backupPath, databasePath, overwrite: true);
                DeleteSidecars(databasePath);
            }
        }

        if (legacyVersion is null)
        {
            throw new InvalidDataException(
                "The database could not be opened with SQLite3MC or recognized as a compatible SQLCipher database. " +
                "The original database was not modified.");
        }

        // A previous interrupted attempt may have left a partial staged database. The source
        // has now been validated, so discard staged artifacts and start from a fresh copy.
        DeleteDatabaseArtifacts(temporaryPath, backupPath);

        await using (var source = CreateConnection(databasePath, password, $"cipher=sqlcipher&legacy={legacyVersion}"))
        {
            await source.OpenAsync(cancellationToken);
            await VerifyQuickCheckAsync(source, cancellationToken);
            await CheckpointWalAsync(source, cancellationToken);
        }

        SqliteConnection.ClearAllPools();
        DeleteSidecars(databasePath);
        File.Copy(databasePath, temporaryPath, overwrite: true);

        try
        {
            await RekeyStagedDatabaseAsync(
                temporaryPath,
                password,
                legacyVersion.Value,
                cancellationToken);

            // Confirm the staged file is readable using the exact connection configuration the
            // application will use after startup, not just through the migration connection.
            await EnsureReadableAsync(
                temporaryPath,
                password,
                $"cipher={TargetCipher}",
                cancellationToken);
            DeleteSidecars(temporaryPath);
            FlushFile(temporaryPath);

            CopyAndFlush(databasePath, backupPath);
            try
            {
                // Both files are in the same directory. This replacement is a same-volume rename;
                // the encrypted original is also retained until the new file passes a final check.
                File.Move(temporaryPath, databasePath, overwrite: true);
                DeleteSidecars(databasePath);
                await EnsureReadableAsync(
                    databasePath,
                    password,
                    $"cipher={TargetCipher}",
                    cancellationToken);
            }
            catch (Exception replacementFailure)
            {
                try
                {
                    DeleteSidecars(databasePath);
                    File.Move(backupPath, databasePath, overwrite: true);
                    await EnsureReadableAsync(
                        databasePath,
                        password,
                        $"cipher=sqlcipher&legacy={legacyVersion}",
                        CancellationToken.None);
                }
                catch (Exception restoreFailure)
                {
                    throw new AggregateException(
                        "SQLite3MC migration failed and the original SQLCipher backup could not be restored.",
                        replacementFailure,
                        restoreFailure);
                }

                throw;
            }

            File.Delete(backupPath);
            return true;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteSidecars(temporaryPath);
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static async Task RekeyStagedDatabaseAsync(
        string databasePath,
        string password,
        int legacyVersion,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(
            databasePath,
            password,
            $"cipher=sqlcipher&legacy={legacyVersion}");
        await connection.OpenAsync(cancellationToken);
        await VerifyQuickCheckAsync(connection, cancellationToken);

        await ExecuteAsync(connection, $"PRAGMA cipher = '{TargetCipher}';", cancellationToken);
        await ExecuteAsync(
            connection,
            $"PRAGMA rekey = '{EscapeSqlLiteral(password)}';",
            cancellationToken);

        await VerifyQuickCheckAsync(connection, cancellationToken);
        await CheckpointWalAsync(connection, cancellationToken);
    }

    private static async Task<int?> DetectSqlCipherLegacyVersionAsync(
        string databasePath,
        string password,
        CancellationToken cancellationToken)
    {
        foreach (var legacyVersion in SqlCipherLegacyVersions)
        {
            if (await IsReadableAsync(
                    databasePath,
                    password,
                    $"cipher=sqlcipher&legacy={legacyVersion}",
                    cancellationToken))
            {
                return legacyVersion;
            }
        }

        return null;
    }

    private static async Task<bool> IsReadableAsync(
        string databasePath,
        string password,
        string cipherOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureReadableAsync(databasePath, password, cipherOptions, cancellationToken);
            return true;
        }
        catch (SqliteException exception) when (IsNotADatabase(exception))
        {
            return false;
        }
    }

    private static async Task EnsureReadableAsync(
        string databasePath,
        string password,
        string cipherOptions,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(databasePath, password, cipherOptions);
        await connection.OpenAsync(cancellationToken);
        await ProbeReadableAsync(connection, cancellationToken);
    }

    private static async Task ProbeReadableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // Keep the already-migrated startup path inexpensive. The regular initializer runs the
        // full quick_check immediately after this cipher probe and before normal use.
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA schema_version;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
            throw new InvalidDataException("SQLite did not return a schema version for the database.");
    }

    private static async Task VerifyQuickCheckAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));

        if (rows.Count != 1 || !string.Equals(rows[0], "ok", StringComparison.OrdinalIgnoreCase))
        {
            var diagnostic = rows.Count == 0 ? "no-result" : string.Join(";", rows.Take(8));
            throw new InvalidDataException(
                $"Database structural integrity verification failed during encryption migration ({diagnostic}).");
        }
    }

    private static async Task CheckpointWalAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("SQLite did not return a WAL checkpoint result.");

        if (reader.GetInt32(0) != 0)
        {
            throw new IOException(
                "The database could not be checkpointed because another SQLite connection is still using it.");
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteConnection CreateConnection(
        string databasePath,
        string password,
        string cipherOptions)
    {
        var connectionString = CreateConnectionString(
            databasePath,
            password,
            cipherOptions,
            SqliteOpenMode.ReadWrite,
            pooling: false);
        return new SqliteConnection(connectionString);
    }

    private static string CreateConnectionString(
        string databasePath,
        string password,
        string cipherOptions,
        SqliteOpenMode mode,
        bool pooling)
    {
        var fileUri = new Uri(Path.GetFullPath(databasePath)).AbsoluteUri;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = $"{fileUri}?{cipherOptions}",
            Password = password,
            Mode = mode,
            Pooling = pooling
        };
        return builder.ToString();
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static bool IsNotADatabase(SqliteException exception) =>
        exception.SqliteErrorCode == SqliteNotADatabaseErrorCode;

    private static void RecoverMissingDatabaseFromBackup(string databasePath, string backupPath)
    {
        if (!File.Exists(databasePath) && File.Exists(backupPath))
        {
            File.Move(backupPath, databasePath);
            DeleteSidecars(databasePath);
        }
    }

    private static void DeleteDatabaseArtifacts(string temporaryPath, string backupPath)
    {
        DeleteSidecars(temporaryPath);
        DeleteSidecars(backupPath);
        if (File.Exists(temporaryPath))
            File.Delete(temporaryPath);
        if (File.Exists(backupPath))
            File.Delete(backupPath);
    }

    private static void DeleteSidecars(string databasePath)
    {
        DeleteIfExists($"{databasePath}-wal");
        DeleteIfExists($"{databasePath}-shm");
        DeleteIfExists($"{databasePath}-journal");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void CopyAndFlush(string sourcePath, string destinationPath)
    {
        File.Copy(sourcePath, destinationPath, overwrite: true);
        FlushFile(destinationPath);
    }

    private static void FlushFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }
}
