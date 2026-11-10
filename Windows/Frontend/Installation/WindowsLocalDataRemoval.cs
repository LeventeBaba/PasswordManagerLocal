namespace PasswordManagerLocal.Windows.Frontend.Installation;

internal static class WindowsLocalDataRemoval
{
    public static void Remove(string directory, WindowsInstanceNames names)
    {
        if (!CheckDirectoryIfPresent(directory))
            return;

        var lockDirectory = Path.GetDirectoryName(names.AgentLockFilePath)!;
        CheckDirectoryIfPresent(lockDirectory);
        // Check even dangling links before the lock helper opens or creates these files.
        EnsureOrdinaryLockFileIfPresent(names.AgentLockFilePath);
        EnsureOrdinaryLockFileIfPresent(names.UiLockFilePath);

        using (var agent = new FileProcessInstanceLock(names.AgentLockFilePath))
        using (var ui = new FileProcessInstanceLock(names.UiLockFilePath))
        {
            if (!agent.IsOwner || !ui.IsOwner)
                throw new InvalidOperationException("Exit Password Manager Local Demo completely from its notification-area icon before deleting local data.");

            // Collect and validate the entire tree before deleting any vault files.
            var entries = new List<FileSystemInfo>();
            CollectEntries(new DirectoryInfo(directory), entries);
            foreach (var entry in entries)
            {
                if (string.Equals(entry.FullName, lockDirectory, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.FullName, names.AgentLockFilePath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.FullName, names.UiLockFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                EnsureOrdinaryDirectory(Path.GetDirectoryName(entry.FullName)!);
                RejectReparsePoint(File.GetAttributes(entry.FullName));
                entry.Delete();
            }
        }

        // Remove only the known lock files and empty directories after releasing
        // ownership. A process racing to start cannot have its new vault deleted.
        EnsureOrdinaryDirectory(lockDirectory);
        File.Delete(names.AgentLockFilePath);
        File.Delete(names.UiLockFilePath);
        Directory.Delete(lockDirectory);
        Directory.Delete(directory);
    }

    private static void CollectEntries(DirectoryInfo directory, List<FileSystemInfo> entries)
    {
        EnsureOrdinaryDirectory(directory.FullName);
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            RejectReparsePoint(entry.Attributes);
            if (entry is DirectoryInfo child)
                CollectEntries(child, entries);

            entries.Add(entry);
        }
    }

    private static bool CheckDirectoryIfPresent(string path)
    {
        try
        {
            EnsureOrdinaryDirectory(path);
            return true;
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException)
        {
            return false;
        }
    }

    private static void EnsureOrdinaryDirectory(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
        {
            var attributes = File.GetAttributes(directory.FullName);
            RejectReparsePoint(attributes);
            if ((attributes & FileAttributes.Directory) == 0)
                throw new IOException("An app data directory was replaced with a file.");
        }
    }

    private static void EnsureOrdinaryLockFileIfPresent(string path)
    {
        try
        {
            RejectReparsePoint(File.GetAttributes(path));
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException)
        {
            // The ordinary app startup creates missing lock files.
        }
    }

    private static void RejectReparsePoint(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Local data removal stopped because the path contains a symbolic link, junction, or another redirected entry.");
    }
}
