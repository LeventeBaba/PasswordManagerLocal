using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Security;

public sealed class Sha256HashBuilder : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _finalized;
    private bool _disposed;

    public void Write(byte value)
    {
        EnsureWritable();
        Span<byte> buffer = stackalloc byte[1];
        buffer[0] = value;
        _hash.AppendData(buffer);
    }

    public void Write(bool value) => Write(value ? (byte)1 : (byte)0);

    public void Write(int value)
    {
        EnsureWritable();
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        _hash.AppendData(buffer);
    }

    public void Write(long value)
    {
        EnsureWritable();
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        _hash.AppendData(buffer);
    }

    public void Write(Guid value)
    {
        EnsureWritable();
        Span<byte> buffer = stackalloc byte[16];
        if (!value.TryWriteBytes(buffer))
            throw new InvalidOperationException("Failed to write GUID bytes for hashing.");

        _hash.AppendData(buffer);
    }

    public void Write(DateTime value) => Write(UtcDateTimeUtil.ToUtc(value).Ticks);

    public void Write(DateTimeOffset value) => Write(UtcDateTimeUtil.ToUtc(value).ToUnixTimeMilliseconds());

    public void Write(DateTimeOffset? value) => Write(value.HasValue ? UtcDateTimeUtil.ToUtc(value.Value).ToUnixTimeMilliseconds() : 0);

    public void WriteBytes(byte[]? value)
    {
        if (value is null)
        {
            Write(0);
            return;
        }

        WriteBytes(value.AsSpan());
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        EnsureWritable();
        Write(value.Length);
        if (!value.IsEmpty)
            _hash.AppendData(value);
    }

    public void WriteString(string? value)
    {
        EnsureWritable();

        var text = value ?? string.Empty;
        var byteCount = Encoding.UTF8.GetByteCount(text);
        Write(byteCount);
        if (byteCount == 0)
            return;

        const int MaxStackUtf8Bytes = 256;
        if (byteCount <= MaxStackUtf8Bytes)
        {
            Span<byte> bytes = stackalloc byte[byteCount];
            try
            {
                var bytesWritten = Encoding.UTF8.GetBytes(text.AsSpan(), bytes);
                if (bytesWritten != byteCount)
                    throw new InvalidOperationException("UTF-8 encoding length changed unexpectedly.");

                _hash.AppendData(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }

            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var bytes = rented.AsSpan(0, byteCount);
            var bytesWritten = Encoding.UTF8.GetBytes(text.AsSpan(), bytes);
            if (bytesWritten != byteCount)
                throw new InvalidOperationException("UTF-8 encoding length changed unexpectedly.");

            _hash.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rented.AsSpan(0, byteCount));
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public byte[] GetHashAndReset()
    {
        EnsureWritable();
        _finalized = true;
        return _hash.GetHashAndReset();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _hash.Dispose();
    }

    private void EnsureWritable()
    {
        if (_disposed || _finalized)
            throw new ObjectDisposedException(nameof(Sha256HashBuilder));
    }
}
