using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.ClientServer;

[TestClass]
public sealed class WindowsIpcMalformedInputTests
{
    [TestMethod]
    public async Task MalformedTypedPayloadReturnsProtocolSafeFailure()
    {
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.SetBackgroundSyncEnabled,
            (context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                context.GetRequiredPayload(
                    WindowsIpcJsonContext.Default.SetBackgroundSyncEnabledRequestDto);
                return Task.FromResult(context.Success());
            });
        await using var session = await IpcTestSession.CreateAsync(new[] { handler });

        var exception = await Assert.ThrowsAsync<IpcRemoteException>(async () =>
            await session.Client.SendAsync(
                IpcOperationId.SetBackgroundSyncEnabled,
                new byte[] { 0xFF, 0x00, 0x11 }));

        Assert.AreEqual(IpcErrorCode.InvalidPayload, exception.Error.ErrorCode);
        Assert.AreEqual("The IPC request payload is invalid.", exception.Error.SafeMessage);
    }

    [TestMethod]
    public async Task InvalidEnvelopeCorrelationIsRejectedWithoutDispatch()
    {
        var dispatchCalls = 0;
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.Ping,
            (context, cancellationToken) =>
            {
                dispatchCalls++;
                return Task.FromResult(context.Success());
            });
        var pair = new InMemoryIpcConnectionPair();
        var serializer = new WindowsIpcSerializer();
        var server = new WindowsIpcServerConnectionSession(
            pair.Server,
            serializer,
            new WindowsIpcRequestDispatcher(new[] { handler }),
            new WindowsIpcServerOptions(
                IpcPeerRole.Agent,
                new[] { IpcPeerRole.TestClient },
                IpcCapabilities.Control));
        var serverTask = server.RunAsync();
        await CompleteRawHandshakeAsync(pair, serializer);
        var request = new IpcRequestEnvelope(55, IpcOperationId.Ping, null);
        var payload = serializer.Serialize(
            request,
            WindowsIpcJsonContext.Default.IpcRequestEnvelope);
        await pair.Client.WriteFrameAsync(CreateFrame(IpcMessageKind.Request, 56, payload));

        var responseFrame = await pair.Client.ReadFrameAsync();
        Assert.IsNotNull(responseFrame);
        var response = serializer.Deserialize(
            responseFrame.Payload,
            WindowsIpcJsonContext.Default.IpcResponseEnvelope);

        Assert.AreEqual(IpcErrorCode.InvalidEnvelope, response.Error?.ErrorCode);
        Assert.AreEqual(0, dispatchCalls);
        await pair.Client.DisposeAsync();
        await serverTask;
    }

    [TestMethod]
    public async Task UndefinedOperationValueIsRejectedAsInvalidEnvelope()
    {
        var pair = new InMemoryIpcConnectionPair();
        var serializer = new WindowsIpcSerializer();
        var server = new WindowsIpcServerConnectionSession(
            pair.Server,
            serializer,
            new WindowsIpcRequestDispatcher(Array.Empty<IWindowsIpcRequestHandler>()),
            new WindowsIpcServerOptions(
                IpcPeerRole.Agent,
                new[] { IpcPeerRole.TestClient },
                IpcCapabilities.Control));
        var serverTask = server.RunAsync();
        await CompleteRawHandshakeAsync(pair, serializer);
        var request = new IpcRequestEnvelope(57, (IpcOperationId)999, null);
        var payload = serializer.Serialize(
            request,
            WindowsIpcJsonContext.Default.IpcRequestEnvelope);
        await pair.Client.WriteFrameAsync(CreateFrame(IpcMessageKind.Request, 57, payload));

        var responseFrame = await pair.Client.ReadFrameAsync();
        Assert.IsNotNull(responseFrame);
        var response = serializer.Deserialize(
            responseFrame.Payload,
            WindowsIpcJsonContext.Default.IpcResponseEnvelope);

        Assert.AreEqual(IpcErrorCode.InvalidEnvelope, response.Error?.ErrorCode);
        await pair.Client.DisposeAsync();
        await serverTask;
    }

    [TestMethod]
    public async Task UnexpectedPayloadForPayloadlessOperationIsRejected()
    {
        await using var session = await IpcTestSession.CreateAsync(
            new IWindowsIpcRequestHandler[] { new PingWindowsIpcRequestHandler() });

        var exception = await Assert.ThrowsAsync<IpcRemoteException>(async () =>
            await session.Client.SendAsync(IpcOperationId.Ping, Array.Empty<byte>()));

        Assert.AreEqual(IpcErrorCode.InvalidPayload, exception.Error.ErrorCode);
    }

    [TestMethod]
    public async Task UnknownEnumValueInTypedPayloadIsRejected()
    {
        var sink = new DelegateUiActivationRequestSink((_, _) => Task.FromResult(true));
        await using var session = await IpcTestSession.CreateAsync(
            new IWindowsIpcRequestHandler[]
            {
                new RequestUiActivationWindowsIpcRequestHandler(sink)
            });
        var request = new UiActivationRequestDto((UiActivationReason)999, true);
        var payload = session.Serializer.Serialize(
            request,
            WindowsIpcJsonContext.Default.UiActivationRequestDto);

        var exception = await Assert.ThrowsAsync<IpcRemoteException>(async () =>
            await session.Client.SendAsync(IpcOperationId.RequestUiActivation, payload));

        Assert.AreEqual(IpcErrorCode.InvalidPayload, exception.Error.ErrorCode);
    }

    [TestMethod]
    public void DuplicateOperationRegistrationIsRejected()
    {
        var first = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.Ping,
            (context, _) => Task.FromResult(context.Success()));
        var second = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.Ping,
            (context, _) => Task.FromResult(context.Success()));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new WindowsIpcRequestDispatcher(new[] { first, second }));
    }

    [TestMethod]
    public async Task InvalidServerResponseFailsPendingRequestAndConnection()
    {
        var pair = new InMemoryIpcConnectionPair();
        var serializer = new WindowsIpcSerializer();
        var client = new WindowsIpcClient(
            pair.Client,
            serializer,
            new WindowsIpcClientOptions(
                IpcPeerRole.TestClient,
                IpcPeerRole.Agent,
                IpcCapabilities.Control,
                Environment.ProcessId,
                0,
                Guid.NewGuid()));
        var handshakeTask = client.HandshakeAsync();
        var requestFrame = await pair.Server.ReadFrameAsync();
        Assert.IsNotNull(requestFrame);
        var handshakeResponse = new IpcHandshakeResponse(
            true,
            WindowsIpcProtocol.CurrentVersion,
            IpcPeerRole.Agent,
            Guid.NewGuid(),
            IpcCapabilities.Control,
            null);
        var handshakePayload = serializer.Serialize(
            handshakeResponse,
            WindowsIpcJsonContext.Default.IpcHandshakeResponse);
        await pair.Server.WriteFrameAsync(
            CreateFrame(IpcMessageKind.HandshakeResponse, 1, handshakePayload));
        await handshakeTask;

        var pendingTask = client.SendAsync(IpcOperationId.Ping);
        var ordinaryRequest = await pair.Server.ReadFrameAsync();
        Assert.IsNotNull(ordinaryRequest);
        var invalidResponse = new IpcResponseEnvelope(
            ordinaryRequest.Header.CorrelationId,
            IsSuccess: true,
            Result: null,
            Error: new IpcError(
                IpcErrorCode.InternalFailure,
                IpcErrorCategory.Internal,
                "invalid",
                ordinaryRequest.Header.CorrelationId,
                DateTimeOffset.UtcNow,
                false,
                false));
        var responsePayload = serializer.Serialize(
            invalidResponse,
            WindowsIpcJsonContext.Default.IpcResponseEnvelope);
        await pair.Server.WriteFrameAsync(
            CreateFrame(
                IpcMessageKind.Response,
                ordinaryRequest.Header.CorrelationId,
                responsePayload));

        await Assert.ThrowsAsync<PasswordManagerLocal.Windows.Ipc.Transport.IpcConnectionClosedException>(
            async () => await pendingTask);
        await client.DisposeAsync();
        await pair.Server.DisposeAsync();
    }

    private static async Task CompleteRawHandshakeAsync(
        InMemoryIpcConnectionPair pair,
        WindowsIpcSerializer serializer)
    {
        var request = new IpcHandshakeRequest(
            WindowsIpcProtocol.CurrentVersion,
            IpcPeerRole.TestClient,
            Environment.ProcessId,
            0,
            Guid.NewGuid(),
            IpcCapabilities.Control);
        var payload = serializer.Serialize(
            request,
            WindowsIpcJsonContext.Default.IpcHandshakeRequest);
        await pair.Client.WriteFrameAsync(CreateFrame(IpcMessageKind.HandshakeRequest, 1, payload));
        Assert.IsNotNull(await pair.Client.ReadFrameAsync());
    }

    private static IpcFrame CreateFrame(
        IpcMessageKind kind,
        long correlationId,
        byte[] payload) =>
        new(
            new IpcFrameHeader(
                WindowsIpcProtocol.CurrentVersion,
                kind,
                IpcFrameFlags.None,
                correlationId,
                payload.Length),
            payload);
}
