using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using PasswordManagerLocal.Windows.Ipc.Validation;

namespace PasswordManagerLocal.Windows.Tests.IPC.ClientServer;

[TestClass]
public sealed class WindowsIpcServerResponseShutdownTests
{
    [TestMethod]
    [Timeout(10_000)]
    public async Task SessionShutdownInterruptsBlockedResponseAndCompletesCleanup()
    {
        var serializer = new WindowsIpcSerializer();
        var connection = new ServerRecordingWindowsIpcConnection(
            blockResponseWrites: true);
        var observer = new RecordingLifecycleObserver();
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.Ping,
            (context, _) => Task.FromResult(context.Success()));
        var server = CreateServer(connection, serializer, handler, observer);
        connection.QueueIncoming(CreateHandshakeRequest(serializer));
        connection.QueueIncoming(CreateRequest(serializer, correlationId: 2));
        var runTask = server.RunAsync();
        await connection.WaitForResponseWriteCallsAsync(1);
        await connection.WaitForStartedWritesAsync(2);

        var disposeTask = server.DisposeAsync().AsTask();
        await disposeTask;
        await runTask;

        Assert.IsTrue(connection.LastResponseWriteToken.IsCancellationRequested);
        Assert.AreEqual(0, server.ActiveRequestCount);
        Assert.IsFalse(connection.IsConnected);
        Assert.AreEqual(
            0,
            connection.CompletedWrites.Count(frame =>
                frame.Header.MessageKind == IpcMessageKind.Response));
        Assert.IsTrue(observer.Notifications.Any(notification =>
            notification.State == IpcConnectionLifecycleState.Disconnected &&
            notification.DisconnectKind == IpcDisconnectKind.ServerCancellation));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RequestCancellationDoesNotInterruptActiveResponseWrite()
    {
        var serializer = new WindowsIpcSerializer();
        var connection = new ServerRecordingWindowsIpcConnection(
            blockResponseWrites: true);
        var requestCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.Ping,
            (context, cancellationToken) =>
            {
                cancellationToken.Register(() => requestCancelled.TrySetResult());
                return Task.FromResult(context.Success());
            });
        var server = CreateServer(connection, serializer, handler);
        connection.QueueIncoming(CreateHandshakeRequest(serializer));
        connection.QueueIncoming(CreateRequest(serializer, correlationId: 2));
        var runTask = server.RunAsync();
        await connection.WaitForResponseWriteCallsAsync(1);
        await connection.WaitForStartedWritesAsync(2);

        connection.QueueIncoming(CreateCancellation(correlationId: 2));
        await requestCancelled.Task;
        Assert.IsFalse(connection.LastResponseWriteToken.IsCancellationRequested);
        Assert.AreEqual(
            0,
            connection.CompletedWrites.Count(frame =>
                frame.Header.MessageKind == IpcMessageKind.Response));

        connection.ReleaseResponseWrites();
        await WaitUntilAsync(() => connection.CompletedWrites.Any(frame =>
            frame.Header.MessageKind == IpcMessageKind.Response));
        connection.CompleteIncoming();
        await runTask;
        await server.DisposeAsync();

        Assert.AreEqual(1, connection.CompletedWrites.Count(frame =>
            frame.Header.MessageKind == IpcMessageKind.Response));
        Assert.AreEqual(0, server.ActiveRequestCount);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RequestCancellationDoesNotSkipAlreadyProducedQueuedResponse()
    {
        var serializer = new WindowsIpcSerializer();
        var connection = new ServerRecordingWindowsIpcConnection(
            blockResponseWrites: true);
        var queuedRequestCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.Ping,
            (context, cancellationToken) =>
            {
                if (context.Request.CorrelationId == 3)
                {
                    cancellationToken.Register(
                        () => queuedRequestCancelled.TrySetResult());
                }

                return Task.FromResult(context.Success());
            });
        var server = CreateServer(connection, serializer, handler);
        connection.QueueIncoming(CreateHandshakeRequest(serializer));
        connection.QueueIncoming(CreateRequest(serializer, correlationId: 2));
        connection.QueueIncoming(CreateRequest(serializer, correlationId: 3));
        var runTask = server.RunAsync();
        await connection.WaitForResponseWriteCallsAsync(2);
        await connection.WaitForStartedWritesAsync(2);

        connection.QueueIncoming(CreateCancellation(correlationId: 3));
        await queuedRequestCancelled.Task;
        connection.ReleaseResponseWrites();
        await WaitUntilAsync(() => connection.CompletedWrites.Count(frame =>
            frame.Header.MessageKind == IpcMessageKind.Response) == 2);
        connection.CompleteIncoming();
        await runTask;
        await server.DisposeAsync();

        var responses = connection.CompletedWrites
            .Where(frame => frame.Header.MessageKind == IpcMessageKind.Response)
            .Select(frame => frame.Header.CorrelationId)
            .ToArray();
        CollectionAssert.AreEquivalent(new long[] { 2, 3 }, responses);
        Assert.AreEqual(0, server.ActiveRequestCount);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ConnectionShutdownPreventsQueuedResponseFromStarting()
    {
        var serializer = new WindowsIpcSerializer();
        var connection = new ServerRecordingWindowsIpcConnection(
            blockResponseWrites: true);
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.Ping,
            (context, _) => Task.FromResult(context.Success()));
        var server = CreateServer(connection, serializer, handler);
        connection.QueueIncoming(CreateHandshakeRequest(serializer));
        connection.QueueIncoming(CreateRequest(serializer, correlationId: 2));
        connection.QueueIncoming(CreateRequest(serializer, correlationId: 3));
        var runTask = server.RunAsync();
        await connection.WaitForResponseWriteCallsAsync(2);
        await connection.WaitForStartedWritesAsync(2);

        await server.DisposeAsync();
        await runTask;

        Assert.AreEqual(1, connection.StartedWrites.Count(frame =>
            frame.Header.MessageKind == IpcMessageKind.Response));
        Assert.AreEqual(0, connection.CompletedWrites.Count(frame =>
            frame.Header.MessageKind == IpcMessageKind.Response));
        Assert.AreEqual(0, server.ActiveRequestCount);
        Assert.IsFalse(connection.IsConnected);
    }

    private static WindowsIpcServerConnectionSession CreateServer(
        ServerRecordingWindowsIpcConnection connection,
        WindowsIpcSerializer serializer,
        IWindowsIpcRequestHandler handler,
        IWindowsIpcConnectionLifecycleObserver? observer = null)
    {
        var validator = new WindowsIpcContractValidator();
        return new WindowsIpcServerConnectionSession(
            connection,
            serializer,
            new WindowsIpcRequestDispatcher(new[] { handler }, validator),
            new WindowsIpcServerOptions(
                IpcPeerRole.Agent,
                new[] { IpcPeerRole.TestClient },
                IpcCapabilities.Control),
            observer is null ? null : new[] { observer },
            contractValidator: validator);
    }

    private static IpcFrame CreateHandshakeRequest(WindowsIpcSerializer serializer)
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
        return CreateFrame(IpcMessageKind.HandshakeRequest, 1, payload);
    }

    private static IpcFrame CreateRequest(
        WindowsIpcSerializer serializer,
        long correlationId)
    {
        var request = new IpcRequestEnvelope(
            correlationId,
            IpcOperationId.Ping,
            Payload: null);
        var payload = serializer.Serialize(
            request,
            WindowsIpcJsonContext.Default.IpcRequestEnvelope);
        return CreateFrame(IpcMessageKind.Request, correlationId, payload);
    }

    private static IpcFrame CreateCancellation(long correlationId) =>
        CreateFrame(
            IpcMessageKind.RequestCancellation,
            correlationId,
            Array.Empty<byte>());

    private static IpcFrame CreateFrame(
        IpcMessageKind messageKind,
        long correlationId,
        byte[] payload) =>
        new(
            new IpcFrameHeader(
                WindowsIpcProtocol.CurrentVersion,
                messageKind,
                IpcFrameFlags.None,
                correlationId,
                payload.Length),
            payload);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        while (!condition())
            await Task.Yield();
    }
}
