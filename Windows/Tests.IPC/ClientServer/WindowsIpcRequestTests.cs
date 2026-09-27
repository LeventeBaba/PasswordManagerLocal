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
                observedCorrelationId = context.Request.CorrelationId;
                return Task.FromResult(context.Success(
                    new PingResponseDto(DateTimeOffset.UtcNow),
                    WindowsIpcJsonContext.Default.PingResponseDto));
            });
        await using var session = await IpcTestSession.CreateAsync(new[] { handler });

        var response = await session.Client.SendAsync(IpcOperationId.Ping);

        Assert.AreEqual(observedCorrelationId, response.CorrelationId);
        Assert.IsTrue(response.IsSuccess);
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
        var request = new IpcRequestEnvelope(9, IpcOperationId.GetAgentStatus, null);
        var requestPayload = serializer.Serialize(
            request,
            WindowsIpcJsonContext.Default.IpcRequestEnvelope);
        await pair.Client.WriteFrameAsync(CreateFrame(IpcMessageKind.Request, 9, requestPayload));

        var responseFrame = await pair.Client.ReadFrameAsync();
        Assert.IsNotNull(responseFrame);
        var response = serializer.Deserialize(
            responseFrame.Payload,
            WindowsIpcJsonContext.Default.IpcResponseEnvelope);

        Assert.AreEqual(9, response.CorrelationId);
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
        var envelope = IpcResponseEnvelope.Success(999, resultPayload);
        var payload = session.Serializer.Serialize(
            envelope,
            WindowsIpcJsonContext.Default.IpcResponseEnvelope);
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
                IpcFrameFlags.None,
                correlationId,
                payload.Length),
            payload);
}
