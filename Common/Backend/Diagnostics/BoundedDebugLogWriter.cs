using System.Globalization;
using System.Text;

namespace PasswordManagerLocal.Common.Backend.Diagnostics;

/// <summary>Best-effort diagnostic retention; these files never contain required recovery evidence.</summary>
internal sealed class BoundedDebugLogWriter
{
    internal const int MaximumFiles = 8;
    internal const int MaximumFileBytes = 4 * 1024 * 1024;
    internal const int MaximumEntryBytes = 64 * 1024;
    private static readonly byte[] TruncationMarker = Encoding.UTF8.GetBytes("\n[Log entry truncated]\n");
    private readonly string _directory;
    private readonly int _maximumFiles;
    private readonly int _maximumFileBytes;
    private readonly int _maximumEntryBytes;
    private string? _currentPath;

    public BoundedDebugLogWriter(string directory,
        int maximumFiles = MaximumFiles,
        int maximumFileBytes = MaximumFileBytes,
        int maximumEntryBytes = MaximumEntryBytes)
    {
        if (maximumFiles <= 0 || maximumFileBytes < maximumEntryBytes ||
            maximumEntryBytes <= TruncationMarker.Length)
            throw new ArgumentOutOfRangeException(nameof(maximumFiles));
        _directory = Path.GetFullPath(directory);
        _maximumFiles = maximumFiles;
        _maximumFileBytes = maximumFileBytes;
        _maximumEntryBytes = maximumEntryBytes;
    }

    public bool TryAppend(string text)
    {
        try
        {
            var buffer = new byte[(int)Math.Min((long)text.Length * 3 + TruncationMarker.Length, _maximumEntryBytes)];
            Encoding.UTF8.GetEncoder().Convert(text.AsSpan(), buffer.AsSpan(0, buffer.Length - TruncationMarker.Length),
                flush: true, out _, out var bytesWritten, out var complete);
            if (!complete)
            {
                TruncationMarker.CopyTo(buffer, bytesWritten);
                bytesWritten += TruncationMarker.Length;
            }

            Directory.CreateDirectory(_directory);
            // No retry: diagnostics must not wait behind another process or affect application work.
            using var fileLock = new FileStream(Path.Combine(_directory, "backend-debug.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);

            var retained = Directory.EnumerateFiles(_directory, "*.log", SearchOption.TopDirectoryOnly)
                .Where(IsOwnedLog).Select(path => new FileInfo(path)).ToList();
            foreach (var oversized in retained.Where(file => file.Length > _maximumFileBytes).ToArray())
            {
                oversized.Delete();
                retained.Remove(oversized);
            }

            var current = retained.Find(file => string.Equals(file.FullName, _currentPath, StringComparison.Ordinal));
            if (current is null || current.Length + bytesWritten > _maximumFileBytes)
            {
                _currentPath = Path.Combine(_directory, $"backend-debug-{Guid.NewGuid():N}.log");
                current = null;
            }

            var totalBytes = retained.Sum(file => file.Length);
            var totalLimit = (long)_maximumFiles * _maximumFileBytes;
            foreach (var oldest in retained.Where(file => file != current)
                         .OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal).ToArray())
            {
                if (retained.Count + (current is null ? 1 : 0) <= _maximumFiles &&
                    totalBytes + bytesWritten <= totalLimit)
                    break;
                var length = oldest.Length;
                oldest.Delete();
                retained.Remove(oldest);
                totalBytes -= length;
            }

            // Never append if retention could not establish room. All cooperating writers hold
            // this same lock through both pruning and append; partial writes also stay in the reserve.
            if (retained.Count + (current is null ? 1 : 0) > _maximumFiles || totalBytes + bytesWritten > totalLimit)
                return false;

            using var stream = new FileStream(_currentPath!, current is null ? FileMode.CreateNew : FileMode.Append,
                FileAccess.Write, FileShare.Read);
            stream.Write(buffer.AsSpan(0, bytesWritten));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsOwnedLog(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        const string prefix = "backend-debug-";
        if (name.StartsWith(prefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(name.AsSpan(prefix.Length), "N", out _))
            return true;

        // Recognize the former timestamp-based names, including their numeric collision suffix.
        const int timestampLength = 23;
        if (name.Length < timestampLength ||
            !DateTime.TryParseExact(name.AsSpan(0, timestampLength), "yyyy-MM-dd_HH-mm-ss.fff",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return false;
        if (name.Length == timestampLength)
            return true;
        var suffix = name.AsSpan(timestampLength);
        return suffix.Length >= 3 && suffix[0] == '_' &&
            int.TryParse(suffix[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index > 0;
    }
}
