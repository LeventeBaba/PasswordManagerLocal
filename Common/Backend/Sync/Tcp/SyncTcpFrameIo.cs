using Google.Protobuf;
using PasswordManagerLocal.Common.Backend.Constants;
using System.Buffers.Binary;

namespace PasswordManagerLocal.Common.Backend.Sync.Tcp;

public static class SyncTcpFrameIo
{
    public static async Task WriteAsync(Stream stream, SyncTcpMessageType type, CancellationToken ct)
    {
        await WriteAsync(stream, type, Array.Empty<byte>(), ct);
    }

    public static async Task WriteAsync(Stream stream, SyncTcpMessageType type, IMessage message, CancellationToken ct)
    {
        await WriteAsync(stream, type, message.ToByteArray(), ct);
    }

    public static async Task<SyncTcpFrame?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var lengthBytes = new byte[4];
        var headerRead = await ReadAtLeastOneThenExactAsync(stream, lengthBytes, ct);
        if (!headerRead)
            return null;

        var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        if (length <= 0 || length > SyncConstants.MaxSyncTcpFrameBytes)
            throw new InvalidDataException("Sync TCP frame length is invalid.");

        var frameBytes = new byte[length];
        await ReadExactAsync(stream, frameBytes, ct);

        var type = (SyncTcpMessageType)frameBytes[0];
        var payload = frameBytes.Length == 1 ? Array.Empty<byte>() : frameBytes[1..];
        return new SyncTcpFrame(type, payload);
    }

    private static async Task WriteAsync(Stream stream, SyncTcpMessageType type, byte[] payload, CancellationToken ct)
    {
        var length = checked(payload.Length + 1);
        if (length > SyncConstants.MaxSyncTcpFrameBytes)
            throw new InvalidDataException("Sync TCP frame is too large.");

        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, length);

        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(new[] { (byte)type }, ct);

        if (payload.Length > 0)
            await stream.WriteAsync(payload, ct);

        await stream.FlushAsync(ct);
    }

    private static async Task<bool> ReadAtLeastOneThenExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0)
                return offset != 0 ? throw new EndOfStreamException() : false;

            offset += read;
        }

        return true;
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0)
                throw new EndOfStreamException();

            offset += read;
        }
    }
}
