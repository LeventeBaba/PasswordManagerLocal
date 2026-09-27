using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Security;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace PasswordManagerLocal.Common.Backend.Utils;

internal static class DataCodec
{
    internal static Task<byte[]> SerializeCompressEncryptAsync<T>(
        T value,
        EncryptionKey key,
        JsonTypeInfo<T> typeInfo,
        int level = 11,
        byte[]? associatedData = null,
        int aesFrameSize = CryptographyConstants.DefaultAesFrameSizeBytes,
        CancellationToken ct = default) where T : class =>
        SerializeCompressEncryptCoreAsync(
            value,
            key,
            (stream, token) => JsonSerializer.SerializeAsync(stream, value, typeInfo, token),
            level,
            associatedData,
            aesFrameSize,
            ct);

    internal static Task<T?> DecryptDecompressDeserializeAsync<T>(
        byte[] blob,
        EncryptionKey key,
        JsonTypeInfo<T> typeInfo,
        byte[]? associatedData = null,
        CancellationToken ct = default) where T : class =>
        DecryptDecompressDeserializeCoreAsync(
            blob,
            key,
            (stream, token) => JsonSerializer.DeserializeAsync(stream, typeInfo, token),
            associatedData,
            ct);

    private static async Task<byte[]> SerializeCompressEncryptCoreAsync<T>(
        T value,
        EncryptionKey key,
        Func<Stream, CancellationToken, Task> serializeAsync,
        int level,
        byte[]? associatedData,
        int aesFrameSize,
        CancellationToken ct) where T : class
    {
        await using var producer = new ChannelStream();
        using var cipherOutput = new MemoryStream();
        var encryptionTask = AES256.EncryptToStreamAsync(producer, cipherOutput, key, associatedData, aesFrameSize);

        try
        {
            await using (var compressionStream = await CompressionUtil.OpenWriteAsync(producer, level, leaveOpen: true))
            {
                UtcDateTimeUtil.NormalizeObjectGraph(value);
                await serializeAsync(compressionStream, ct);
                await compressionStream.FlushAsync(ct);
            }

            producer.CompleteWriting();
            await encryptionTask;
            return cipherOutput.ToArray();
        }
        catch (Exception ex)
        {
            producer.CompleteWriting(ex);
            try
            {
                await encryptionTask;
            }
            catch
            {
                // Preserve the original serialization/compression failure.
            }

            throw;
        }
    }

    private static async Task<T?> DecryptDecompressDeserializeCoreAsync<T>(
        byte[] blob,
        EncryptionKey key,
        Func<Stream, CancellationToken, ValueTask<T?>> deserializeAsync,
        byte[]? associatedData,
        CancellationToken ct) where T : class
    {
        using var input = new MemoryStream(blob, writable: false);
        await using var plaintextPipe = new ChannelStream();
        var decryptionTask = DecryptIntoPipeAsync(input, plaintextPipe, key, associatedData);

        try
        {
            await using var decompressionStream = await CompressionUtil.OpenReadAsync(plaintextPipe, leaveOpen: true);
            var value = await deserializeAsync(decompressionStream, ct);

            // Consume the complete authenticated and compressed stream before accepting
            // the decoded object. This both authenticates every AES-GCM frame and prevents
            // the bounded producer pipe from being left blocked if the JSON reader finishes
            // before the producer has emitted its final segment.
            await DrainSensitiveStreamAsync(decompressionStream, ct);
            await decryptionTask;

            return value is null ? null : UtcDateTimeUtil.NormalizeObjectGraph(value);
        }
        catch
        {
            // A failed decoder may stop reading while decryption is still blocked on a full
            // bounded pipe. Dispose the pipe first so queued plaintext is zeroed and the
            // producer is released, then observe its completion without changing the codec's
            // existing null-on-invalid-input contract.
            await plaintextPipe.DisposeAsync();
            await decryptionTask;
            return null;
        }
    }


    private static async Task DrainSensitiveStreamAsync(Stream stream, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct) != 0)
            {
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task DecryptIntoPipeAsync(
        Stream input,
        ChannelStream plaintextPipe,
        EncryptionKey key,
        byte[]? associatedData)
    {
        try
        {
            await AES256.DecryptToStreamAsync(input, plaintextPipe, key, associatedData);
            plaintextPipe.CompleteWriting();
        }
        catch (Exception ex)
        {
            // Preserve the existing codec contract: decryption/decoding failures are
            // observed by the reader and result in a null decoded value rather than
            // escaping as a separate background-task exception.
            plaintextPipe.CompleteWriting(ex);
        }
    }
}
