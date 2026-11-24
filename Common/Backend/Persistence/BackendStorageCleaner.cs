using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Configuration;

namespace PasswordManagerLocal.Common.Backend.Persistence;

internal sealed class BackendStorageCleaner
{
    private readonly BackendStoragePaths _paths;

    public BackendStorageCleaner(BackendStoragePaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public void ClearSqlitePools() => SqliteConnection.ClearAllPools();

    public async Task DeleteDatabaseFilesAsync(CancellationToken cancellationToken)
    {
        using var fileLock = await StorageFileLock.AcquireAsync($"{_paths.DatabaseConfigPath}.lock", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Fixed, empty, non-secret marker: a later process must not open partially
        // deleted storage or generate replacement keys after an interrupted reset.
        using (var marker = new FileStream(_paths.DatabaseResetPendingPath, FileMode.OpenOrCreate,
                   FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.WriteThrough))
            marker.Flush(flushToDisk: true);

        // Irreversible boundary. No caller cancellation or automatic cleanup retry
        // may detach the owner from the outcome after this point.
        try
        {
            DeleteDatabaseFilesCore();
            File.Delete(_paths.DatabaseResetPendingPath);
        }
        catch (Exception exception)
        {
            throw new DatabaseResetIncompleteException(exception);
        }
    }

    private void DeleteDatabaseFilesCore()
    {
        DeleteFileIfExists(_paths.DatabasePath);
        DeleteFileIfExists(_paths.DatabaseMigrationTempPath);
        DeleteFileIfExists(_paths.DatabaseMigrationBackupPath);
        DeleteFileIfExists(_paths.DatabaseWalPath);
        DeleteFileIfExists(_paths.DatabaseShmPath);
        DeleteFileIfExists(_paths.DatabaseJournalPath);
        DeleteFileIfExists($"{_paths.DatabaseMigrationTempPath}-wal");
        DeleteFileIfExists($"{_paths.DatabaseMigrationTempPath}-shm");
        DeleteFileIfExists($"{_paths.DatabaseMigrationTempPath}-journal");
        DeleteFileIfExists($"{_paths.DatabaseMigrationBackupPath}-wal");
        DeleteFileIfExists($"{_paths.DatabaseMigrationBackupPath}-shm");
        DeleteFileIfExists($"{_paths.DatabaseMigrationBackupPath}-journal");
        DeleteFileIfExists(_paths.DatabaseConfigPath);
        DeleteFileIfExists($"{_paths.DatabaseConfigPath}.tmp");

        foreach (var temporaryConfigPath in Directory.EnumerateFiles(
                     _paths.RootDirectory,
                     _paths.GetTemporaryDatabaseConfigSearchPattern()))
        {
            DeleteFileIfExists(temporaryConfigPath);
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        File.Delete(path);
    }
}
