using PasswordManagerLocal.Common.Backend.Constants;

namespace PasswordManagerLocal.Common.Backend.Configuration;

public sealed class BackendStoragePaths
{
    public BackendStoragePaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        if (!Path.IsPathFullyQualified(rootDirectory))
            throw new ArgumentException("The backend storage root must be an absolute path.", nameof(rootDirectory));

        RootDirectory = Directory.CreateDirectory(rootDirectory).FullName;
        DatabasePath = Path.Combine(RootDirectory, ApplicationFileNames.DbFileName);
        DatabaseWalPath = $"{DatabasePath}-wal";
        DatabaseShmPath = $"{DatabasePath}-shm";
        DatabaseJournalPath = $"{DatabasePath}-journal";
        DatabaseConfigPath = Path.Combine(RootDirectory, ApplicationFileNames.DbConfigFileName);
        LogsDirectory = Path.Combine(RootDirectory, ApplicationFileNames.LogsFolderName);
    }

    public string RootDirectory { get; }
    public string DatabasePath { get; }
    public string DatabaseWalPath { get; }
    public string DatabaseShmPath { get; }
    public string DatabaseJournalPath { get; }
    public string DatabaseConfigPath { get; }
    public string LogsDirectory { get; }

    public string GetTemporaryDatabaseConfigSearchPattern() =>
        $"{ApplicationFileNames.DbConfigFileName}.*.tmp";
}
