namespace PasswordManagerLocal.Common.Preferences;

internal sealed class ApplicationPreferencesFileLock : IAsyncDisposable
{
    private FileStream? _stream;

    public ApplicationPreferencesFileLock(FileStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
        return ValueTask.CompletedTask;
    }
}
