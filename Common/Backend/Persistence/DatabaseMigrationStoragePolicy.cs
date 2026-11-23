using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Contracts.Errors;

namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>Finite copy admission and a conservative free-space check before legacy conversion.</summary>
internal sealed class DatabaseMigrationStoragePolicy
{
    private readonly Func<string, long> _availableSpace;
    public long MaximumDatabaseBytes { get; }
    internal SqlitePhysicalLimits PhysicalLimits { get; }

    public DatabaseMigrationStoragePolicy(long maximumDatabaseBytes = StorageCapacityConstants.MainDatabaseBytes,
        Func<string, long>? availableSpace = null, SqlitePhysicalLimits? physicalLimits = null)
    {
        if (maximumDatabaseBytes <= 0 || maximumDatabaseBytes > StorageCapacityConstants.MainDatabaseBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumDatabaseBytes));
        PhysicalLimits = physicalLimits ?? SqlitePhysicalLimits.Default;
        PhysicalLimits.Validate();
        MaximumDatabaseBytes = Math.Min(maximumDatabaseBytes, PhysicalLimits.MainBytes);
        _availableSpace = availableSpace ?? ReadAvailableSpace;
    }

    public long RequireFileWithinLimit(string path)
    {
        var length = new FileInfo(path).Length;
        if (length > MaximumDatabaseBytes) throw new StorageCapacityExceededException();
        return length;
    }

    public long RequiredWorkingBytes(long sourceBytes)
    {
        if (sourceBytes < 0 || sourceBytes > MaximumDatabaseBytes) throw new StorageCapacityExceededException();
        // Staged image + backup + two source-sized rewrite allowances. This is an admission
        // estimate, not a filesystem reservation or a bound on SQLite's internal temporary files.
        return checked(4 * sourceBytes + StorageCapacityConstants.MigrationWorkspaceReserveBytes);
    }

    public void RequireWorkingSpace(string directory, long sourceBytes)
    {
        var required = RequiredWorkingBytes(sourceBytes);
        try
        {
            if (_availableSpace(directory) < required)
                throw new StorageCapacityExceededException();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new StorageCapacityExceededException(exception);
        }
    }

    private static long ReadAvailableSpace(string directory)
    {
        var full = Path.GetFullPath(directory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // Unix application data can be on a separate mount. Select the containing mount with
        // the longest path rather than assuming the filesystem root owns the directory.
        var drive = DriveInfo.GetDrives().Where(candidate =>
            string.Equals(full, Path.TrimEndingDirectorySeparator(candidate.Name), comparison) ||
            full.StartsWith(Path.EndsInDirectorySeparator(candidate.Name) ? candidate.Name : candidate.Name + Path.DirectorySeparatorChar, comparison))
            .OrderByDescending(candidate => candidate.Name.Length).FirstOrDefault();
        if (drive is null) throw new IOException("The database filesystem's available space could not be determined.");
        return drive.AvailableFreeSpace;
    }
}
