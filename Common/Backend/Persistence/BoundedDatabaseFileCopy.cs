using PasswordManagerLocal.Common.Contracts.Errors;

namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>Creates one finite encrypted database copy; the authoritative source is never replaced here.</summary>
internal static class BoundedDatabaseFileCopy
{
    public static async Task CopyAndFlushAsync(string sourcePath, string destinationPath,
        long maximumBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var expectedBytes = source.Length;
        ValidateLength(expectedBytes, maximumBytes);
        var created = false;
        try
        {
            await using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                created = true;
                await CopyContentAsync(source, destination, expectedBytes, maximumBytes, ct);
                ct.ThrowIfCancellationRequested();
                await destination.FlushAsync(ct);
                destination.Flush(flushToDisk: true);
            }
        }
        catch
        {
            // A failed CreateNew must never delete a pre-existing backup. A failed cleanup may
            // leave only this fixed, bounded staging slot for the next verified startup to reclaim.
            if (created)
            {
                try { File.Delete(destinationPath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
            throw;
        }
    }

    internal static async Task CopyContentAsync(Stream source, Stream destination,
        long expectedBytes, long maximumBytes, CancellationToken ct)
    {
        ValidateLength(expectedBytes, maximumBytes);
        var buffer = new byte[64 * 1024];
        var remaining = expectedBytes;
        while (remaining > 0)
        {
            var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (count == 0) throw new EndOfStreamException("The database changed during its staged copy.");
            await destination.WriteAsync(buffer.AsMemory(0, count), ct);
            remaining -= count;
        }
        // Detect growth without ever writing a byte beyond the admitted source image.
        if (await source.ReadAsync(buffer.AsMemory(0, 1), ct) != 0)
            throw new IOException("The database changed during its staged copy.");
    }

    private static void ValidateLength(long length, long maximumBytes)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (length < 0 || length > maximumBytes) throw new StorageCapacityExceededException();
    }
}
