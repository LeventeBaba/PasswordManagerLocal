using System.Buffers.Binary;

namespace PasswordManagerLocal.Windows.Ipc.Protocol;

public sealed class IpcFrameCodec
{
    private readonly int _maximumPayloadSize;

    public IpcFrameCodec(int maximumPayloadSize = WindowsIpcProtocol.MaximumPayloadSize)
    {
        if (maximumPayloadSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadSize));

        _maximumPayloadSize = maximumPayloadSize;
    }

    public async ValueTask<IpcFrame?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var headerBytes = new byte[WindowsIpcProtocol.HeaderSize];
        var firstRead = await stream.ReadAsync(
            headerBytes.AsMemory(0, 1),
            cancellationToken);
        if (firstRead == 0)
            return null;

        try
        {
            await ReadExactlyAsync(
                stream,
                headerBytes.AsMemory(1),
                IpcProtocolErrorCode.TruncatedHeader,
                "The IPC frame header was truncated.",
                cancellationToken);
        }
        catch (IpcProtocolException)
        {
            throw;
        }

        var header = ParseHeader(headerBytes);
        var payload = new byte[header.PayloadLength];
        if (payload.Length > 0)
        {
            await ReadExactlyAsync(
                stream,
                payload,
                IpcProtocolErrorCode.TruncatedPayload,
                "The IPC frame payload was truncated.",
                cancellationToken);
        }

        return new IpcFrame(header, payload);
    }

    public async ValueTask WriteAsync(
        Stream stream,
        IpcFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(frame.Header);
        ArgumentNullException.ThrowIfNull(frame.Payload);

        ValidateHeader(frame.Header);
        if (frame.Header.PayloadLength != frame.Payload.Length)
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.InvalidPayloadLength,
                "The IPC frame payload length does not match the header.");
        }

        var headerBytes = new byte[WindowsIpcProtocol.HeaderSize];
        WriteHeader(frame.Header, headerBytes);
        await stream.WriteAsync(headerBytes, cancellationToken);
        if (frame.Payload.Length > 0)
            await stream.WriteAsync(frame.Payload, cancellationToken);
    }

    public IpcFrameHeader ParseHeader(ReadOnlySpan<byte> headerBytes)
    {
        if (headerBytes.Length != WindowsIpcProtocol.HeaderSize)
            throw new ArgumentException("The IPC frame header has an invalid size.", nameof(headerBytes));

        var magic = BinaryPrimitives.ReadUInt32BigEndian(headerBytes[0..4]);
        if (magic != WindowsIpcProtocol.Magic)
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.InvalidMagic,
                "The IPC frame magic value is invalid.");
        }

        var header = new IpcFrameHeader(
            BinaryPrimitives.ReadInt32BigEndian(headerBytes[4..8]),
            (IpcMessageKind)BinaryPrimitives.ReadInt32BigEndian(headerBytes[8..12]),
            (IpcFrameFlags)BinaryPrimitives.ReadInt32BigEndian(headerBytes[12..16]),
            BinaryPrimitives.ReadInt64BigEndian(headerBytes[16..24]),
            BinaryPrimitives.ReadInt32BigEndian(headerBytes[24..28]));

        ValidateHeader(header);
        return header;
    }

    public void WriteHeader(IpcFrameHeader header, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (destination.Length < WindowsIpcProtocol.HeaderSize)
            throw new ArgumentException("The destination is too small for an IPC header.", nameof(destination));

        ValidateHeader(header);
        BinaryPrimitives.WriteUInt32BigEndian(destination[0..4], WindowsIpcProtocol.Magic);
        BinaryPrimitives.WriteInt32BigEndian(destination[4..8], header.ProtocolVersion);
        BinaryPrimitives.WriteInt32BigEndian(destination[8..12], (int)header.MessageKind);
        BinaryPrimitives.WriteInt32BigEndian(destination[12..16], (int)header.Flags);
        BinaryPrimitives.WriteInt64BigEndian(destination[16..24], header.CorrelationId);
        BinaryPrimitives.WriteInt32BigEndian(destination[24..28], header.PayloadLength);
    }

    public void ValidateHeader(IpcFrameHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);

        if (header.ProtocolVersion != WindowsIpcProtocol.CurrentVersion)
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.UnsupportedVersion,
                $"IPC protocol version {header.ProtocolVersion} is not supported.");
        }

        if (!Enum.IsDefined(header.MessageKind))
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.UnknownMessageKind,
                "The IPC message kind is unknown.");
        }

        if (header.Flags != IpcFrameFlags.None)
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.InvalidFlags,
                "The IPC frame contains unsupported flags.");
        }

        if (header.CorrelationId <= 0)
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.InvalidCorrelationId,
                "The IPC correlation ID must be positive.");
        }

        if (header.PayloadLength < 0 || header.PayloadLength > _maximumPayloadSize)
        {
            throw new IpcProtocolException(
                IpcProtocolErrorCode.InvalidPayloadLength,
                "The IPC frame payload length is outside the permitted range.");
        }
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        IpcProtocolErrorCode errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination[offset..], cancellationToken);
            if (read == 0)
                throw new IpcProtocolException(errorCode, errorMessage);

            offset += read;
        }
    }
}
