using System.IO.Compression;

namespace PasswordManagerLocal.Common.Backend.Utils;

internal static class CompressionUtil
{
    internal static async Task<Stream> CompressAsync(Stream dataStream, int level = 11)
    {
        if (dataStream == null) throw new ArgumentNullException(nameof(dataStream));
        var output = new MemoryStream();
        await using (var compressor = new BrotliStream(output, MapBrotliLevel(level), leaveOpen: true))
        {
            await dataStream.CopyToAsync(compressor);
        }
        output.Position = 0;
        return output;
    }

    internal static async Task WriteCompressedAsync(Stream input, Stream output, int level = 11)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (output == null) throw new ArgumentNullException(nameof(output));
        await using var compressor = new BrotliStream(output, MapBrotliLevel(level), leaveOpen: true);
        await input.CopyToAsync(compressor);
    }

    internal static Task<Stream> DecompressAsync(Stream compressedStream)
    {
        if (compressedStream == null) throw new ArgumentNullException(nameof(compressedStream));
        var decompressor = new BrotliStream(compressedStream, CompressionMode.Decompress, leaveOpen: false);
        return Task.FromResult<Stream>(decompressor);
    }

    internal static async Task WriteDecompressedAsync(Stream compressedStream, Stream output)
    {
        if (compressedStream == null) throw new ArgumentNullException(nameof(compressedStream));
        if (output == null) throw new ArgumentNullException(nameof(output));
        await using var decompressor = new BrotliStream(compressedStream, CompressionMode.Decompress, leaveOpen: true);
        await decompressor.CopyToAsync(output);
    }

    internal static Task<Stream> OpenWriteAsync(Stream output, int level = 11, bool leaveOpen = false)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));
        return Task.FromResult<Stream>(new BrotliStream(output, MapBrotliLevel(level), leaveOpen));
    }

    internal static Task<Stream> OpenReadAsync(Stream input, bool leaveOpen = false)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        return Task.FromResult<Stream>(new BrotliStream(input, CompressionMode.Decompress, leaveOpen));
    }

    private static CompressionLevel MapBrotliLevel(int q)
    {
        if (q <= 1) return CompressionLevel.Fastest;
        if (q >= 9) return CompressionLevel.SmallestSize;
        return CompressionLevel.Optimal;
    }
}