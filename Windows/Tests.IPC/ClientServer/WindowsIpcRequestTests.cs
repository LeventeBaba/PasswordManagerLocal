using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using PasswordManagerLocal.Windows.Ipc.Transport;

namespace PasswordManagerLocal.Windows.Tests.IPC.ClientServer;

[TestClass]
public sealed class WindowsIpcRequestTests
{
    [TestMethod]
    public async Task PingRequestSucceedsAndCorrelationIdIsPreserved()
    {
        long observedCorrelationId = 0;
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.Ping,
            (context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                observedCorrelationId = context.CorrelationId;
                return Task.FromResult(context.Success(
                    new PingResponseDto(DateTimeOffset.UtcNow),
                    WindowsIpcJsonContext.Default.PingResponseDto));
            });
        await using var session = await IpcTestSession.CreateAsync(new[] { handler });

        var response = await session.Client.SendWithTransmissionStateAsync(IpcOperationId.Ping);

        Assert.AreEqual(observedCorrelationId, response.CorrelationId);
        Assert.IsTrue(response.Response.IsSuccess);
    }

    [TestMethod]
    public async Task MultipleConcurrentRequestsCanCompleteOutOfOrder()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.SetBackgroundSyncEnabled,
            async (context, cancellationToken) =>
            {
                var request = context.GetRequiredPayload(
                    WindowsIpcJsonContext.Default.SetBackgroundSyncEnabledRequestDto);
                if (!request.IsEnabled)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }

                var state = request.IsEnabled
                    ? new WindowsBackgroundSyncStateDto(true, true, true, true, false,
                        WindowsBackgroundSyncConsistency.Operational,
                        WindowsBackgroundSyncFailureKind.None, null)
                    : new WindowsBackgroundSyncStateDto(false, false, false, false, false,
                        WindowsBackgroundSyncConsistency.Disabled,
                        WindowsBackgroundSyncFailureKind.None, null);
                return context.Success(
                    state,
                    WindowsIpcJsonContext.Default.WindowsBackgroundSyncStateDto);
            });
        await using var session = await IpcTestSession.CreateAsync(new[] { handler });
        var controlClient = new WindowsIpcControlClient(session.Client, session.Serializer);

        var firstTask = controlClient.SetBackgroundSyncEnabledAsync(
            new SetBackgroundSyncEnabledRequestDto(false));
        await firstStarted.Task;
        var secondTask = controlClient.SetBackgroundSyncEnabledAsync(
            new SetBackgroundSyncEnabledRequestDto(true));
        var secondResult = await secondTask;

        Assert.IsTrue(secondResult.IsEnabled);
        Assert.IsFalse(firstTask.IsCompleted);

        releaseFirst.TrySetResult();
        var firstResult = await firstTask;
        Assert.IsFalse(firstResult.IsEnabled);
    }

    [TestMethod]
    public async Task UnknownOperationReturnsStructuredError()
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
        var requestPayload = new IpcFramePayloadCodec(serializer).EncodeRequest(
            new IpcRequestEnvelope(IpcOperationId.GetAgentStatus, null),
            9);
        await pair.Client.WriteFrameAsync(CreateFrame(IpcMessageKind.Request, 9, requestPayload));

        var responseFrame = await pair.Client.ReadFrameAsync();
        Assert.IsNotNull(responseFrame);
        var response = new IpcFramePayloadCodec(serializer).DecodeResponse(
            responseFrame.Header.CorrelationId,
            responseFrame.Payload);

        Assert.AreEqual(9, responseFrame.Header.CorrelationId);
        Assert.AreEqual(IpcErrorCode.UnknownOperation, response.Error?.ErrorCode);
        await pair.Client.DisposeAsync();
        await serverTask;
    }

    [TestMethod]
    public async Task HandlerExceptionReturnsStructuredErrorWithoutExceptionDetails()
    {
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.Ping,
            (_, _) => throw new InvalidOperationException("sensitive internal path C:\\secret"));
        await using var session = await IpcTestSession.CreateAsync(new[] { handler });

        var exception = await Assert.ThrowsAsync<IpcRemoteException>(async () =>
            await session.Client.SendAsync(IpcOperationId.Ping));

        Assert.AreEqual(IpcErrorCode.HandlerFailed, exception.Error.ErrorCode);
        Assert.AreEqual("The IPC operation failed.", exception.Error.SafeMessage);
        Assert.IsFalse(exception.Error.SafeMessage.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task EndpointOnlyResponseKindsAreRejectedForControlOperations()
    {
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.Ping,
            (_, _) => Task.FromResult(IpcResponseEnvelope.EndpointFailure(new byte[] { 1 })));
        await using var session = await IpcTestSession.CreateAsync(new[] { handler });

        var exception = await Assert.ThrowsAsync<IpcProtocolException>(async () =>
            await session.Client.SendAsync(IpcOperationId.Ping));

        Assert.AreEqual(IpcProtocolErrorCode.UnexpectedMessageKind, exception.ErrorCode);
    }

    [TestMethod]
    public async Task ResponseBufferIsClearedAfterEncodingWhileReceivedCopyRemainsReadable()
    {
        var body = new byte[] { 11, 22, 33, 44 };
        var expected = body.ToArray();
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.Ping,
            (_, _) => Task.FromResult(IpcResponseEnvelope.Success(body)));
        await using var session = await IpcTestSession.CreateAsync(new[] { handler });

        var response = await session.Client.SendAsync(IpcOperationId.Ping);

        CollectionAssert.AreEqual(expected, response.Result!);
        CollectionAssert.AreEqual(new byte[body.Length], body);
    }

    [TestMethod]
    public async Task CancelledRequestCancelsOnlyThatRequest()
    {
        var callCount = 0;
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.Ping,
            async (context, cancellationToken) =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

                return context.Success();
            });
        await using var session = await IpcTestSession.CreateAsync(new[] { handler });
        using var cancellationSource = new CancellationTokenSource();
        var cancelledTask = session.Client.SendAsync(
            IpcOperationId.Ping,
            cancellationToken: cancellationSource.Token);
        cancellationSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelledTask);
        var followUp = await session.Client.SendAsync(IpcOperationId.Ping);
        Assert.IsTrue(followUp.IsSuccess);
    }

    [TestMethod]
    public async Task UnknownCorrelationResponseDoesNotCorruptPendingRequests()
    {
        await using var session = await IpcTestSession.CreateAsync(
            new IWindowsIpcRequestHandler[] { new PingWindowsIpcRequestHandler() });
        var resultPayload = session.Serializer.Serialize(
            new PingResponseDto(DateTimeOffset.UtcNow),
            WindowsIpcJsonContext.Default.PingResponseDto);
        var payload = new IpcFramePayloadCodec(session.Serializer).EncodeResponse(
            IpcResponseEnvelope.Success(resultPayload),
            999);
        await session.Pair.Server.WriteFrameAsync(
            CreateFrame(IpcMessageKind.Response, 999, payload));

        var response = await session.Client.SendAsync(IpcOperationId.Ping);

        Assert.IsTrue(response.IsSuccess);
    }

    [TestMethod]
    public async Task DisconnectFailsAllPendingRequests()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.Ping,
            async (context, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return context.Success();
            });
        await using var session = await IpcTestSession.CreateAsync(new[] { handler });
        var pendingTask = session.Client.SendAsync(IpcOperationId.Ping);
        await started.Task;

        session.Pair.Server.Fault(new IOException("pipe failed"));

        await Assert.ThrowsAsync<IpcConnectionClosedException>(async () => await pendingTask);
        Assert.AreEqual(0, session.Client.PendingRequestCount);
    }

    private static async Task CompleteRawHandshakeAsync(
        InMemoryIpcConnectionPair pair,
        WindowsIpcSerializer serializer)
    {
        var request = new IpcHandshakeRequest(
            IpcPeerRole.TestClient,
            Environment.ProcessId,
            0,
            Guid.NewGuid(),
            IpcCapabilities.Control);
        var payload = serializer.Serialize(
            request,
            WindowsIpcJsonContext.Default.IpcHandshakeRequest);
        await pair.Client.WriteFrameAsync(CreateFrame(IpcMessageKind.HandshakeRequest, 1, payload));
        var response = await pair.Client.ReadFrameAsync();
        Assert.IsNotNull(response);
        Assert.AreEqual(IpcMessageKind.HandshakeResponse, response.Header.MessageKind);
    }

    private static IpcFrame CreateFrame(
        IpcMessageKind kind,
        long correlationId,
        byte[] payload) =>
        new(
            new IpcFrameHeader(
                WindowsIpcProtocol.CurrentVersion,
                kind,
                correlationId,
                payload.Length),
            payload);
}
