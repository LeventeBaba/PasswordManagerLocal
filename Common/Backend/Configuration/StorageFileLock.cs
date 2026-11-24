using System.Diagnostics;

namespace PasswordManagerLocal.Common.Backend.Configuration;

/// <summary>Serializes replacement of one auxiliary file across host/store instances.</summary>
internal static class StorageFileLock
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    public static FileStream Acquire(string path)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try { return Open(path); }
            catch (IOException) when (elapsed.Elapsed < Timeout) { Thread.Sleep(RetryDelay); }
        }
    }

    public static async Task<FileStream> AcquireAsync(string path, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return Open(path); }
            catch (IOException) when (elapsed.Elapsed < Timeout)
            {
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    // Keep the empty lock file after close: unlinking it could split competing owners.
    private static FileStream Open(string path) =>
        new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
}
