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

    public void DeleteDatabaseFiles()
    {
        using var fileLock = StorageFileLock.Acquire($"{_paths.DatabaseConfigPath}.lock");
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
        if (File.Exists(path))
            File.Delete(path);
    }
}
