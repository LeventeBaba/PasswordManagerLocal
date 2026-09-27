using PasswordManagerLocal.Common.Backend.Constants;
using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Backend.Security;

internal static class AES256
{

    private const uint Magic = 0x4D434741;
    private const byte Version = 1;
    private const int HeaderSize = 4 + 1 + 8 + 4;
    private const int FrameMetadataSize = 8;

    internal static byte[] GenerateKey()
    {
        var key = new byte[CryptographyConstants.Aes256KeySizeInBytes];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    internal static async Task<byte[]> EncryptAsync(
        byte[] data,
        EncryptionKey key,
        byte[]? associatedData = null,
        int frameSize = CryptographyConstants.DefaultAesFrameSizeBytes)
    {
        var effectiveFrameSize = data.Length == 0 ? 1 : Math.Min(frameSize, data.Length);
        using var input = new MemoryStream(data, writable: false);
        using var output = new MemoryStream(data.Length + 64);
        await EncryptToStreamAsync(input, output, key, associatedData, effectiveFrameSize);
        return output.ToArray();
    }

    internal static async Task<byte[]> DecryptAsync(byte[] blob, EncryptionKey key, byte[]? associatedData = null)
    {
        using var input = new MemoryStream(blob, writable: false);
        using var output = new MemoryStream(Math.Max(0, blob.Length - 37));
        await DecryptToStreamAsync(input, output, key, associatedData);
        return output.ToArray();
    }

    internal static async Task<Stream> EncryptAsync(
        Stream input,
        EncryptionKey key,
        byte[]? associatedData = null,
        int frameSize = CryptographyConstants.DefaultAesFrameSizeBytes)
    {
        var output = new MemoryStream();
        await EncryptToStreamAsync(input, output, key, associatedData, frameSize);
        output.Position = 0;
        return output;
    }

    internal static async Task<Stream> DecryptAsync(Stream input, EncryptionKey key, byte[]? associatedData = null)
    {
        var output = new MemoryStream();
        await DecryptToStreamAsync(input, output, key, associatedData);
        output.Position = 0;
        return output;
    }

    internal static async Task EncryptToStreamAsync(
        Stream input,
        Stream output,
        EncryptionKey key,
        byte[]? associatedData = null,
        int frameSize = CryptographyConstants.DefaultAesFrameSizeBytes)
    {
        if (frameSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameSize));

        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), Magic);
        header[4] = Version;
        RandomNumberGenerator.Fill(header.AsSpan(5, 8));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(13, 4), frameSize);
        await output.WriteAsync(header, 0, header.Length);

        var buffer = ArrayPool<byte>.Shared.Rent(frameSize);
        var cipher = ArrayPool<byte>.Shared.Rent(frameSize);
        var aadLength = HeaderSize + FrameMetadataSize + (associatedData?.Length ?? 0);
        var aadBuffer = ArrayPool<byte>.Shared.Rent(aadLength);
        var tag = new byte[CryptographyConstants.AesGcmTagSizeInBytes];
        var nonce = new byte[CryptographyConstants.AesGcmNonceSizeInBytes];
        header.AsSpan(5, 8).CopyTo(nonce);
        InitializeAadBuffer(aadBuffer, header, associatedData);

        var lengthBuffer = new byte[sizeof(int)];
        var frameIndex = 0;
        var keyBytes = key.ExportCopy();

        try
        {
            using var gcm = new AesGcm(keyBytes, CryptographyConstants.AesGcmTagSizeInBytes);
            while (true)
            {
                var read = await input.ReadAsync(buffer, 0, buffer.Length);
                if (read <= 0)
                    break;

                BinaryPrimitives.WriteInt32LittleEndian(nonce.AsSpan(8, 4), frameIndex);
                WriteFrameMetadata(aadBuffer, frameIndex, read);
                gcm.Encrypt(
                    nonce,
                    buffer.AsSpan(0, read),
                    cipher.AsSpan(0, read),
                    tag,
                    aadBuffer.AsSpan(0, aadLength));

                BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, read);
                await output.WriteAsync(lengthBuffer, 0, lengthBuffer.Length);
                await output.WriteAsync(cipher, 0, read);
                await output.WriteAsync(tag, 0, CryptographyConstants.AesGcmTagSizeInBytes);
                frameIndex++;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
            ArrayPool<byte>.Shared.Return(aadBuffer, clearArray: true);
            ArrayPool<byte>.Shared.Return(cipher, clearArray: true);
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    internal static async Task DecryptToStreamAsync(
        Stream input,
        Stream output,
        EncryptionKey key,
        byte[]? associatedData = null)
    {
        var header = new byte[HeaderSize];
        await ReadExactAsync(input, header, 0, header.Length);

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        if (magic != Magic)
            throw new CryptographicException("Invalid header.");

        var version = header[4];
        if (version != Version)
            throw new CryptographicException("Unsupported version.");

        var frameSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(13, 4));
        if (frameSize <= 0)
            throw new CryptographicException("Invalid frame size.");

        var cipher = ArrayPool<byte>.Shared.Rent(frameSize);
        var plaintext = ArrayPool<byte>.Shared.Rent(frameSize);
        var aadLength = HeaderSize + FrameMetadataSize + (associatedData?.Length ?? 0);
        var aadBuffer = ArrayPool<byte>.Shared.Rent(aadLength);
        var tag = new byte[CryptographyConstants.AesGcmTagSizeInBytes];
        var nonce = new byte[CryptographyConstants.AesGcmNonceSizeInBytes];
        header.AsSpan(5, 8).CopyTo(nonce);
        InitializeAadBuffer(aadBuffer, header, associatedData);

        var lengthBuffer = new byte[sizeof(int)];
        var frameIndex = 0;
        var keyBytes = key.ExportCopy();

        try
        {
            using var gcm = new AesGcm(keyBytes, CryptographyConstants.AesGcmTagSizeInBytes);
            while (true)
            {
                var lengthRead = await input.ReadAsync(lengthBuffer, 0, lengthBuffer.Length);
                if (lengthRead == 0)
                    break;
                if (lengthRead != lengthBuffer.Length)
                    throw new CryptographicException("Corrupted stream.");

                var chunkLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
                if (chunkLength < 0 || chunkLength > frameSize)
                    throw new CryptographicException("Invalid chunk length.");

                await ReadExactAsync(input, cipher, 0, chunkLength);
                await ReadExactAsync(input, tag, 0, CryptographyConstants.AesGcmTagSizeInBytes);

                BinaryPrimitives.WriteInt32LittleEndian(nonce.AsSpan(8, 4), frameIndex);
                WriteFrameMetadata(aadBuffer, frameIndex, chunkLength);
                gcm.Decrypt(
                    nonce,
                    cipher.AsSpan(0, chunkLength),
                    tag,
                    plaintext.AsSpan(0, chunkLength),
                    aadBuffer.AsSpan(0, aadLength));

                await output.WriteAsync(plaintext, 0, chunkLength);
                frameIndex++;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
            ArrayPool<byte>.Shared.Return(aadBuffer, clearArray: true);
            ArrayPool<byte>.Shared.Return(plaintext, clearArray: true);
            ArrayPool<byte>.Shared.Return(cipher, clearArray: true);
        }
    }

    private static void InitializeAadBuffer(byte[] aadBuffer, byte[] header, byte[]? associatedData)
    {
        header.CopyTo(aadBuffer, 0);
        if (associatedData is not null)
            associatedData.CopyTo(aadBuffer, HeaderSize + FrameMetadataSize);
    }

    private static void WriteFrameMetadata(byte[] aadBuffer, int frameIndex, int chunkLength)
    {
        BinaryPrimitives.WriteInt32LittleEndian(aadBuffer.AsSpan(HeaderSize, sizeof(int)), frameIndex);
        BinaryPrimitives.WriteInt32LittleEndian(aadBuffer.AsSpan(HeaderSize + sizeof(int), sizeof(int)), chunkLength);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, int offset, int count)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead);
            if (read <= 0)
                throw new EndOfStreamException();
            totalRead += read;
        }
    }
}
