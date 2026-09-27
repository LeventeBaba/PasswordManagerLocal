using System.Diagnostics;
using System.Text;

namespace PasswordManagerLocal.Common.Preferences;

public sealed class PhysicalApplicationPreferencesFileSystem : IApplicationPreferencesFileSystem
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(25);

    public bool FileExists(string path) => File.Exists(path);

    public Task<string> ReadAllTextAsync(
        string path,
        CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public async Task WriteAllTextAsync(
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    public void ReplaceFile(string sourcePath, string destinationPath) =>
        File.Move(sourcePath, destinationPath, overwrite: true);

    public void DeleteFile(string path) => File.Delete(path);

    public async Task<IAsyncDisposable> AcquireExclusiveLockAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
                return new ApplicationPreferencesFileLock(stream);
            }
            catch (IOException) when (started.Elapsed < LockTimeout)
            {
                await Task.Delay(LockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
