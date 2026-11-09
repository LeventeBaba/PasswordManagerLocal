using PasswordManagerLocal.Common.Backend.Hosting;

namespace PasswordManagerLocal.Common.Tests.Fakes;

internal sealed class FakeBackgroundSyncSettingsFileSystem : IBackgroundSyncSettingsFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    public Exception? ReplaceFailure { get; set; }

    public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

    public string ReadAllText(string path) => _files[Normalize(path)];

    public void CreateDirectory(string path)
    {
    }

    public void WriteAllText(string path, string contents) => SetFile(path, contents);

    public void ReplaceFile(string sourcePath, string destinationPath)
    {
        if (ReplaceFailure is not null)
            throw ReplaceFailure;

        var source = Normalize(sourcePath);
        _files[Normalize(destinationPath)] = _files[source];
        _files.Remove(source);
    }

    public void DeleteFile(string path) => _files.Remove(Normalize(path));

    public void SetFile(string path, string contents) =>
        _files[Normalize(path)] = contents;

    public int TemporaryFileCount => _files.Keys.Count(path => path.EndsWith(".tmp", StringComparison.Ordinal));

    private static string Normalize(string path) => Path.GetFullPath(path);
}
