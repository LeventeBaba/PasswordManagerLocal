using PasswordManagerLocal.Common.Preferences;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeApplicationPreferencesFileSystem : IApplicationPreferencesFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Exception? ReplaceFailure { get; set; }

    public Exception? LockFailure { get; set; }

    public int ReplaceCount { get; private set; }

    public int TemporaryFileCount
    {
        get
        {
            lock (_gate)
                return _files.Keys.Count(path => path.EndsWith(".tmp", StringComparison.Ordinal));
        }
    }

    public bool FileExists(string path)
    {
        lock (_gate)
            return _files.ContainsKey(Normalize(path));
    }

    public Task<string> ReadAllTextAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(_files[Normalize(path)]);
    }

    public void CreateDirectory(string path)
    {
    }

    public Task WriteAllTextAsync(
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            _files[Normalize(path)] = contents;
        return Task.CompletedTask;
    }

    public void ReplaceFile(string sourcePath, string destinationPath)
    {
        lock (_gate)
        {
            if (ReplaceFailure is not null)
                throw ReplaceFailure;

            var source = Normalize(sourcePath);
            var destination = Normalize(destinationPath);
            _files[destination] = _files[source];
            _files.Remove(source);
            ReplaceCount++;
        }
    }

    public void DeleteFile(string path)
    {
        lock (_gate)
            _files.Remove(Normalize(path));
    }

    public Task<IAsyncDisposable> AcquireExclusiveLockAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (LockFailure is not null)
            return Task.FromException<IAsyncDisposable>(LockFailure);
        return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
    }

    public void SetFile(string path, string contents)
    {
        lock (_gate)
            _files[Normalize(path)] = contents;
    }

    public string GetFile(string path)
    {
        lock (_gate)
            return _files[Normalize(path)];
    }

    private static string Normalize(string path) => Path.GetFullPath(path);

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
