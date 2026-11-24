using PasswordManagerLocal.Common.Backend.Configuration.BackgroundSync;

namespace PasswordManagerLocal.Common.Tests.Fakes;

internal sealed class FakeBackgroundSyncSettingsFileSystem : IBackgroundSyncSettingsFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    public Exception? ReplaceFailure { get; set; }
    public Exception? DeleteFailure { get; set; }
    public Exception? LockFailure { get; set; }
    public HashSet<string> WrittenTemporaryPaths { get; } = [];

    public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

    public string ReadAllText(string path) => _files[Normalize(path)];

    public void CreateDirectory(string path)
    {
    }

    public void WriteAllText(string path, string contents)
    {
        WrittenTemporaryPaths.Add(Normalize(path));
        SetFile(path, contents);
    }

    public void ReplaceFile(string sourcePath, string destinationPath)
    {
        if (ReplaceFailure is not null)
            throw ReplaceFailure;

        var source = Normalize(sourcePath);
        _files[Normalize(destinationPath)] = _files[source];
        _files.Remove(source);
    }

    public void DeleteFile(string path)
    {
        if (DeleteFailure is not null)
            throw DeleteFailure;
        _files.Remove(Normalize(path));
    }

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern) =>
        _files.Keys.Where(path => Path.GetDirectoryName(path) == Path.GetFullPath(directory) &&
            System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(searchPattern, Path.GetFileName(path))).ToArray();

    public Task<IAsyncDisposable> AcquireExclusiveLockAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return LockFailure is null
            ? Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable())
            : Task.FromException<IAsyncDisposable>(LockFailure);
    }

    public void SetFile(string path, string contents) =>
        _files[Normalize(path)] = contents;

    public int TemporaryFileCount => _files.Keys.Count(path => path.EndsWith(".tmp", StringComparison.Ordinal));

    private static string Normalize(string path) => Path.GetFullPath(path);

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
