namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class TemporaryDirectory : IDisposable
{
    private int _disposed;

    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "PasswordManagerLocal.Phase9",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || !Directory.Exists(Path))
            return;

        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception exception)
        {
            throw new IOException($"Failed to remove the Phase 9 temporary directory '{Path}'.", exception);
        }
    }
}
