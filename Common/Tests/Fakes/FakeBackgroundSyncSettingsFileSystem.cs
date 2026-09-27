using PasswordManagerLocal.Common.Backend.Hosting;

namespace PasswordManagerLocal.Common.Tests.Fakes;

internal sealed class FakeBackgroundSyncSettingsFileSystem : IBackgroundSyncSettingsFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    public Exception? ReadFailure { get; set; }
    public Exception? WriteFailure { get; set; }
    public Exception? ReplaceFailure { get; set; }
    public int CreateDirectoryCount { get; private set; }
    public int WriteCount { get; private set; }
    public int ReplaceCount { get; private set; }
    public int DeleteCount { get; private set; }

    public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

    public string ReadAllText(string path)
    {
        if (ReadFailure is not null)
            throw ReadFailure;
        return _files[Normalize(path)];
    }

    public void CreateDirectory(string path) => CreateDirectoryCount++;

    public void WriteAllText(string path, string contents)
    {
        WriteCount++;
        if (WriteFailure is not null)
            throw WriteFailure;
        _files[Normalize(path)] = contents;
    }

    public void ReplaceFile(string sourcePath, string destinationPath)
    {
        ReplaceCount++;
        if (ReplaceFailure is not null)
            throw ReplaceFailure;

        var source = Normalize(sourcePath);
        _files[Normalize(destinationPath)] = _files[source];
        _files.Remove(source);
    }

    public void DeleteFile(string path)
    {
        DeleteCount++;
        _files.Remove(Normalize(path));
    }

    public void SetFile(string path, string contents) =>
        _files[Normalize(path)] = contents;

    public string? TryGetFile(string path) =>
        _files.TryGetValue(Normalize(path), out var contents) ? contents : null;

    public int TemporaryFileCount => _files.Keys.Count(path => path.EndsWith(".tmp", StringComparison.Ordinal));

    private static string Normalize(string path) => Path.GetFullPath(path);
}
