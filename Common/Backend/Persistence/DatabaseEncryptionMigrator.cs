using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Configuration;

namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>
/// Moves existing SQLCipher databases to SQLite3MC's default ChaCha20 cipher before the backend
/// creates any pooled connections. The original database is left untouched until a staged copy
/// has been re-keyed and verified with the target cipher.
/// </summary>
internal static class DatabaseEncryptionMigrator
{
    private const string TargetCipher = "chacha20";
    private const int SqliteCorruptErrorCode = 11;
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
        CancellationToken cancellationToken = default,
        DatabaseMigrationStoragePolicy? storagePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await MigrateCoreAsync(paths, password, storagePolicy ?? new(), cancellationToken);
        }
        catch (SqliteException exception) when (SqliteCapacityError.IsCapacityFailure(exception))
        {
            throw SqliteCapacityError.Wrap(exception);
        }
    }

    private static async Task<bool> MigrateCoreAsync(BackendStoragePaths paths, string password,
        DatabaseMigrationStoragePolicy storage, CancellationToken cancellationToken)
    {
        var databasePath = paths.DatabasePath;
        var temporaryPath = paths.DatabaseMigrationTempPath;
        var backupPath = paths.DatabaseMigrationBackupPath;

        SqliteConnection.ClearAllPools();
        await RecoverMissingDatabaseFromBackupAsync(databasePath, backupPath, password, storage, cancellationToken);

        if (!File.Exists(databasePath))
        {
            if (HasArtifacts(temporaryPath) || HasArtifacts(backupPath) || HasSidecars(databasePath))
                throw new DatabaseUnreadableException(new InvalidDataException(
                    "The main database is missing while migration or journal recovery artifacts remain. Existing files were preserved."));
            return false;
        }

        if ((HasArtifacts(temporaryPath) || HasArtifacts(backupPath)) && storage.RequireFileWithinLimit(databasePath) == 0)
            throw new DatabaseUnreadableException(new InvalidDataException(
                "An empty main image cannot authorize discarding recovery candidates. Existing files were preserved."));

        if (await IsReadableAsync(databasePath, password, $"cipher={TargetCipher}", storage, cancellationToken))
        {
            if (HasArtifacts(temporaryPath) || HasArtifacts(backupPath))
            {
                // A readable header is not sufficient proof that a backup is redundant.
                await VerifyDatabaseAsync(databasePath, password, $"cipher={TargetCipher}", storage, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                DeleteDatabaseArtifacts(temporaryPath, backupPath);
            }
            return false;
        }

        var legacyVersion = await DetectSqlCipherLegacyVersionAsync(
            databasePath,
            password,
            storage,
            cancellationToken);

        if (legacyVersion is null && File.Exists(backupPath))
        {
            if (HasSidecars(databasePath))
                throw new DatabaseUnreadableException(new InvalidDataException(
                    "The unreadable database has journal recovery artifacts. Existing files were preserved."));
            legacyVersion = await DetectSqlCipherLegacyVersionAsync(
                backupPath,
                password,
                storage,
                cancellationToken);
            if (legacyVersion is not null)
            {
                await VerifyDatabaseAsync(backupPath, password, $"cipher=sqlcipher&legacy={legacyVersion}", storage, cancellationToken);
                storage.RequireWorkingSpace(paths.RootDirectory, storage.RequireFileWithinLimit(backupPath));
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(backupPath, databasePath, overwrite: true);
                DeleteSidecars(databasePath);
                DeleteSidecars(backupPath);
            }
        }

        if (legacyVersion is null)
        {
            throw new DatabaseUnreadableException(new InvalidDataException(
                "The database could not be opened with SQLite3MC or recognized as a compatible SQLCipher database. " +
                "The original database was not modified."));
        }

        await using (var source = CreateConnection(databasePath, password, $"cipher=sqlcipher&legacy={legacyVersion}", storage))
        {
            await source.OpenAsync(cancellationToken);
            await VerifyQuickCheckAsync(source, cancellationToken);
            await RequireDatabaseSchemaAsync(source, cancellationToken);
            await CheckpointWalAsync(source, cancellationToken);
        }

        SqliteConnection.ClearAllPools();
        var sourceBytes = storage.RequireFileWithinLimit(databasePath);
        storage.RequireWorkingSpace(paths.RootDirectory, sourceBytes);
        cancellationToken.ThrowIfCancellationRequested();
        // Only complete structural verification, checkpoint and workspace admission permit
        // retiring an earlier attempt's copies. Failed admission leaves all recovery files intact.
        DeleteDatabaseArtifacts(temporaryPath, backupPath);
        DeleteSidecars(databasePath);

        try
        {
            await BoundedDatabaseFileCopy.CopyAndFlushAsync(databasePath, temporaryPath,
                storage.MaximumDatabaseBytes, cancellationToken);
            await RekeyStagedDatabaseAsync(
                temporaryPath,
                password,
                legacyVersion.Value,
                storage,
                cancellationToken);

            // Confirm the staged file is readable using the exact connection configuration the
            // application will use after startup, not just through the migration connection.
            await EnsureReadableAsync(
                temporaryPath,
                password,
                $"cipher={TargetCipher}",
                storage,
                cancellationToken);
            DeleteSidecars(temporaryPath);
            FlushFile(temporaryPath);

            await BoundedDatabaseFileCopy.CopyAndFlushAsync(databasePath, backupPath,
                storage.MaximumDatabaseBytes, cancellationToken);
            await VerifyDatabaseAsync(backupPath, password, $"cipher=sqlcipher&legacy={legacyVersion}", storage, cancellationToken);
            try
            {
                // Both files are in the same directory. This replacement is a same-volume rename;
                // the encrypted original is also retained until the new file passes a final check.
                File.Move(temporaryPath, databasePath, overwrite: true);
                DeleteSidecars(databasePath);
                await VerifyDatabaseAsync(
                    databasePath,
                    password,
                    $"cipher={TargetCipher}",
                    storage,
                    cancellationToken);
            }
            catch (Exception replacementFailure)
            {
                try
                {
                    DeleteSidecars(databasePath);
                    File.Move(backupPath, databasePath, overwrite: true);
                    await VerifyDatabaseAsync(
                        databasePath,
                        password,
                        $"cipher=sqlcipher&legacy={legacyVersion}",
                        storage,
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
        DatabaseMigrationStoragePolicy storage,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(
            databasePath,
            password,
            $"cipher=sqlcipher&legacy={legacyVersion}", storage);
        await connection.OpenAsync(cancellationToken);
        await VerifyQuickCheckAsync(connection, cancellationToken);

        await ExecuteAsync(connection, $"PRAGMA cipher = '{TargetCipher}';", cancellationToken);
        await ExecuteAsync(
            connection,
            $"PRAGMA rekey = '{EscapeSqlLiteral(password)}';",
            cancellationToken);

        // Reapply using the resulting cipher's actual page size, then verify before promotion.
        SqliteStorageMaintenance.ConfigureConnection(connection, storage.MaximumDatabaseBytes);
        await VerifyQuickCheckAsync(connection, cancellationToken);
        await CheckpointWalAsync(connection, cancellationToken);
    }

    private static async Task<int?> DetectSqlCipherLegacyVersionAsync(
        string databasePath,
        string password,
        DatabaseMigrationStoragePolicy storage,
        CancellationToken cancellationToken)
    {
        foreach (var legacyVersion in SqlCipherLegacyVersions)
        {
            if (await IsReadableAsync(
                    databasePath,
                    password,
                    $"cipher=sqlcipher&legacy={legacyVersion}",
                    storage,
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
        DatabaseMigrationStoragePolicy storage,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureReadableAsync(databasePath, password, cipherOptions, storage, cancellationToken);
            return true;
        }
        catch (SqliteException exception) when (IsUnreadableWithCipher(exception))
        {
            return false;
        }
    }

    private static async Task EnsureReadableAsync(
        string databasePath,
        string password,
        string cipherOptions,
        DatabaseMigrationStoragePolicy storage,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(databasePath, password, cipherOptions, storage);
        await connection.OpenAsync(cancellationToken);
        await ProbeReadableAsync(connection, cancellationToken);
    }

    private static async Task VerifyDatabaseAsync(string databasePath, string password, string cipherOptions,
        DatabaseMigrationStoragePolicy storage, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(databasePath, password, cipherOptions, storage);
        await connection.OpenAsync(cancellationToken);
        await VerifyQuickCheckAsync(connection, cancellationToken);
        await RequireDatabaseSchemaAsync(connection, cancellationToken);
        await CheckpointWalAsync(connection, cancellationToken);
        storage.RequireFileWithinLimit(databasePath);
    }

    private static async Task RequireDatabaseSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // SQLite considers an empty file structurally valid. It cannot dominate an existing
        // staged/backup vault or authorize replacing a missing database with an empty image.
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND rootpage > 0);";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 1)
            throw new DatabaseUnreadableException(new InvalidDataException(
                "The database image has no application schema. Existing recovery files were preserved."));
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
            throw new DatabaseUnreadableException(
                new InvalidDataException("SQLite did not return a schema version for the database."));
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
            throw new DatabaseUnreadableException(new InvalidDataException(
                $"Database structural integrity verification failed during encryption migration ({diagnostic})."));
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
        string cipherOptions,
        DatabaseMigrationStoragePolicy storage)
    {
        storage.RequireFileWithinLimit(databasePath);
        var connectionString = CreateConnectionString(
            databasePath,
            password,
            cipherOptions,
            SqliteOpenMode.ReadWrite,
            pooling: false);
        var options = new SqliteConnectionStringBuilder(connectionString) { DefaultTimeout = 1 };
        return new SqliteConnectionFactory(options.ToString(), maximumDatabaseBytes: storage.MaximumDatabaseBytes,
            physicalLimits: storage.PhysicalLimits)
            .CreateConnection();
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

    private static bool IsUnreadableWithCipher(SqliteException exception) =>
        exception.SqliteErrorCode is SqliteCorruptErrorCode or SqliteNotADatabaseErrorCode;

    private static async Task RecoverMissingDatabaseFromBackupAsync(string databasePath, string backupPath,
        string password, DatabaseMigrationStoragePolicy storage, CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath) && File.Exists(backupPath))
        {
            if (storage.RequireFileWithinLimit(backupPath) == 0)
                throw new DatabaseUnreadableException(new InvalidDataException(
                    "An empty migration backup cannot replace the missing database. Existing files were preserved."));
            if (HasSidecars(databasePath))
                throw new DatabaseUnreadableException(new InvalidDataException(
                    "The missing database has journal recovery artifacts. Existing files were preserved."));
            string cipherOptions;
            if (await IsReadableAsync(backupPath, password, $"cipher={TargetCipher}", storage, cancellationToken))
                cipherOptions = $"cipher={TargetCipher}";
            else if (await DetectSqlCipherLegacyVersionAsync(backupPath, password, storage, cancellationToken) is int legacy)
                cipherOptions = $"cipher=sqlcipher&legacy={legacy}";
            else
                throw new DatabaseUnreadableException(new InvalidDataException("The migration backup could not be verified. Existing files were preserved."));
            await VerifyDatabaseAsync(backupPath, password, cipherOptions, storage, cancellationToken);
            File.Move(backupPath, databasePath);
            DeleteSidecars(backupPath);
        }
    }

    private static bool HasArtifacts(string path) => File.Exists(path) || HasSidecars(path);
    private static bool HasSidecars(string path) => File.Exists($"{path}-wal") ||
        File.Exists($"{path}-shm") || File.Exists($"{path}-journal");

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

    private static void FlushFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }
}
