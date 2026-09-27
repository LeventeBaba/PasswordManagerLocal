using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Configuration;

namespace PasswordManagerLocal.Common.Backend.Hosting;

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
        DeleteFileIfExists(_paths.DatabasePath);
        DeleteFileIfExists(_paths.DatabaseWalPath);
        DeleteFileIfExists(_paths.DatabaseShmPath);
        DeleteFileIfExists(_paths.DatabaseJournalPath);
        DeleteFileIfExists(_paths.DatabaseConfigPath);

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
