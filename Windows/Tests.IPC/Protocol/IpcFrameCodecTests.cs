using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using System.Buffers.Binary;

namespace PasswordManagerLocal.Windows.Tests.IPC.Protocol;

[TestClass]
public sealed class IpcFrameCodecTests
{
    [TestMethod]
    public void ProtocolIdentifiersRemainStableForVersionThree()
    {
        Assert.AreEqual(3, WindowsIpcProtocol.CurrentVersion);
        Assert.AreEqual(28, WindowsIpcProtocol.HeaderSize);
        Assert.AreEqual(1, (int)IpcMessageKind.HandshakeRequest);
        Assert.AreEqual(5, (int)IpcMessageKind.RequestCancellation);
        Assert.AreEqual(1, (int)IpcOperationId.Ping);
        Assert.AreEqual(24, (int)IpcOperationId.RequestAgentExit);
        Assert.AreEqual(31, (int)IpcOperationId.SetBackgroundSyncEnabled);
        Assert.AreEqual(32, (int)IpcOperationId.ReloadApplicationPreferences);
    }

    [TestMethod]
    public async Task ValidFrameRoundTrips()
    {
        var codec = new IpcFrameCodec();
        var payload = new byte[] { 1, 2, 3, 4 };
        var frame = new IpcFrame(
            new IpcFrameHeader(
                WindowsIpcProtocol.CurrentVersion,
                IpcMessageKind.Request,
                IpcFrameFlags.None,
                42,
                payload.Length),
            payload);
        await using var stream = new MemoryStream();

        await codec.WriteAsync(stream, frame);
        stream.Position = 0;
        var restored = await codec.ReadAsync(stream);

        Assert.IsNotNull(restored);
        Assert.AreEqual(frame.Header, restored.Header);
        CollectionAssert.AreEqual(payload, restored.Payload);
    }

    [TestMethod]
    public async Task InvalidMagicIsRejected()
    {
        var bytes = CreateHeaderBytes(
            WindowsIpcProtocol.CurrentVersion,
            IpcMessageKind.Request,
            IpcFrameFlags.None,
            1,
            0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 0x01020304);

        var exception = await Assert.ThrowsAsync<IpcProtocolException>(async () =>
        {
            await using var stream = new MemoryStream(bytes);
            await new IpcFrameCodec().ReadAsync(stream);
        });

        Assert.AreEqual(IpcProtocolErrorCode.InvalidMagic, exception.ErrorCode);
    }

    [TestMethod]
    public async Task UnsupportedVersionIsRejected()
    {
        var bytes = CreateHeaderBytes(
            WindowsIpcProtocol.CurrentVersion + 1,
            IpcMessageKind.Request,
            IpcFrameFlags.None,
            1,
            0);

        var exception = await Assert.ThrowsAsync<IpcProtocolException>(async () =>
        {
            await using var stream = new MemoryStream(bytes);
            await new IpcFrameCodec().ReadAsync(stream);
        });

        Assert.AreEqual(IpcProtocolErrorCode.UnsupportedVersion, exception.ErrorCode);
    }

    [TestMethod]
    public async Task NegativeImpossibleAndOversizedPayloadLengthsAreRejectedBeforePayloadRead()
    {
        await AssertInvalidPayloadLengthAsync(-1, WindowsIpcProtocol.MaximumPayloadSize);
        await AssertInvalidPayloadLengthAsync(
            WindowsIpcProtocol.MaximumPayloadSize + 1,
            WindowsIpcProtocol.MaximumPayloadSize);
        await AssertInvalidPayloadLengthAsync(17, 16);
    }

    [TestMethod]
    public async Task TruncatedHeaderIsRejected()
    {
        await using var stream = new MemoryStream(new byte[] { 1, 2, 3 });

        var exception = await Assert.ThrowsAsync<IpcProtocolException>(async () =>
            await new IpcFrameCodec().ReadAsync(stream));

        Assert.AreEqual(IpcProtocolErrorCode.TruncatedHeader, exception.ErrorCode);
    }

    [TestMethod]
    public async Task TruncatedPayloadIsRejected()
    {
        var header = CreateHeaderBytes(
            WindowsIpcProtocol.CurrentVersion,
            IpcMessageKind.Response,
            IpcFrameFlags.None,
            5,
            4);
        var bytes = header.Concat(new byte[] { 1, 2 }).ToArray();
        await using var stream = new MemoryStream(bytes);

        var exception = await Assert.ThrowsAsync<IpcProtocolException>(async () =>
            await new IpcFrameCodec().ReadAsync(stream));

        Assert.AreEqual(IpcProtocolErrorCode.TruncatedPayload, exception.ErrorCode);
    }

    [TestMethod]
    public async Task UnknownMessageKindAndInvalidFlagsAreRejected()
    {
        await AssertHeaderFailureAsync(
            CreateHeaderBytes(
                WindowsIpcProtocol.CurrentVersion,
                (IpcMessageKind)999,
                IpcFrameFlags.None,
                1,
                0),
            IpcProtocolErrorCode.UnknownMessageKind);
        await AssertHeaderFailureAsync(
            CreateHeaderBytes(
                WindowsIpcProtocol.CurrentVersion,
                IpcMessageKind.Request,
                (IpcFrameFlags)1,
                1,
                0),
            IpcProtocolErrorCode.InvalidFlags);
    }

    [TestMethod]
    public async Task CancellationInterruptsRead()
    {
        await using var stream = new BlockingReadStream();
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new IpcFrameCodec().ReadAsync(stream, cancellationSource.Token));
    }

    [TestMethod]
    public async Task CancellationInterruptsWrite()
    {
        await using var stream = new BlockingWriteStream();
        using var cancellationSource = new CancellationTokenSource();
        var frame = new IpcFrame(
            new IpcFrameHeader(
                WindowsIpcProtocol.CurrentVersion,
                IpcMessageKind.Request,
                IpcFrameFlags.None,
                1,
                0),
            Array.Empty<byte>());
        var writeTask = new IpcFrameCodec().WriteAsync(
            stream,
            frame,
            cancellationSource.Token).AsTask();
        cancellationSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await writeTask);
    }

    private static async Task AssertInvalidPayloadLengthAsync(
        int payloadLength,
        int maximumPayloadLength)
    {
        var bytes = CreateHeaderBytes(
            WindowsIpcProtocol.CurrentVersion,
            IpcMessageKind.Request,
            IpcFrameFlags.None,
            1,
            payloadLength);
        await using var stream = new MemoryStream(bytes);

        var exception = await Assert.ThrowsAsync<IpcProtocolException>(async () =>
            await new IpcFrameCodec(maximumPayloadLength).ReadAsync(stream));

        Assert.AreEqual(IpcProtocolErrorCode.InvalidPayloadLength, exception.ErrorCode);
    }

    private static async Task AssertHeaderFailureAsync(
        byte[] bytes,
        IpcProtocolErrorCode expectedError)
    {
        await using var stream = new MemoryStream(bytes);
        var exception = await Assert.ThrowsAsync<IpcProtocolException>(async () =>
            await new IpcFrameCodec().ReadAsync(stream));
        Assert.AreEqual(expectedError, exception.ErrorCode);
    }

    private static byte[] CreateHeaderBytes(
        int protocolVersion,
        IpcMessageKind messageKind,
        IpcFrameFlags flags,
        long correlationId,
        int payloadLength)
    {
        var bytes = new byte[WindowsIpcProtocol.HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), WindowsIpcProtocol.Magic);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4, 4), protocolVersion);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), (int)messageKind);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12, 4), (int)flags);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(16, 8), correlationId);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(24, 4), payloadLength);
        return bytes;
    }
}
