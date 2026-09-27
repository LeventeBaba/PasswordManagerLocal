using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using PasswordManagerLocal.Windows.Ipc.Transport;

namespace PasswordManagerLocal.Windows.Tests.IPC.ClientServer;

[TestClass]
public sealed class WindowsIpcClientLifecycleHardeningTests
{
    [TestMethod]
    [Timeout(10_000)]
    public async Task DisposeWhileHandshakeWriteIsBlockedCompletesWithoutSemaphoreFailure()
    {
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeCallCount = 0;
        var connection = new DelegateWindowsIpcConnection(
            _ => new ValueTask<IpcFrame?>((IpcFrame?)null),
            async (_, cancellationToken) =>
            {
                writeStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            () =>
            {
                Interlocked.Increment(ref disposeCallCount);
                return ValueTask.CompletedTask;
            });
        var client = CreateClient(connection);
        var handshakeTask = client.HandshakeAsync();
        await writeStarted.Task;

        var firstDispose = client.DisposeAsync().AsTask();
        var secondDispose = client.DisposeAsync().AsTask();

        var handshakeFailure = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await handshakeTask);
        await firstDispose;
        await secondDispose;

        Assert.AreNotEqual(typeof(ObjectDisposedException), handshakeFailure.GetType());
        Assert.AreEqual(1, disposeCallCount);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task DisposeWhileHandshakeReadIsBlockedCompletesWithoutSemaphoreFailure()
    {
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new DelegateWindowsIpcConnection(
            async cancellationToken =>
            {
                readStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            },
            (_, _) => ValueTask.CompletedTask,
            () => ValueTask.CompletedTask);
        var client = CreateClient(connection);
        var handshakeTask = client.HandshakeAsync();
        await readStarted.Task;

        var disposeTask = client.DisposeAsync().AsTask();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await handshakeTask);
        await disposeTask;
        Assert.IsFalse(client.IsHandshakeComplete);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task DisposeImmediatelyBeforeHandshakeCompletionRejectsCompletionDeterministically()
    {
        var serializer = new WindowsIpcSerializer();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionId = Guid.NewGuid();
        var response = CreateHandshakeResponse(serializer, connectionId);
        var connection = new DelegateWindowsIpcConnection(
            async _ =>
            {
                readStarted.TrySetResult();
                await releaseResponse.Task;
                return response;
            },
            (_, _) => ValueTask.CompletedTask,
            () => ValueTask.CompletedTask);
        var client = CreateClient(connection, serializer);
        var handshakeTask = client.HandshakeAsync();
        await readStarted.Task;

        var disposeTask = client.DisposeAsync().AsTask();
        releaseResponse.TrySetResult();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await handshakeTask);
        await disposeTask;
        Assert.IsFalse(client.IsHandshakeComplete);
        Assert.IsNull(client.ServerConnectionId);
    }


    [TestMethod]
    public async Task MissingRequiredServerCapabilityRejectsHandshakeWithoutFallback()
    {
        var serializer = new WindowsIpcSerializer();
        var response = CreateHandshakeResponse(serializer, Guid.NewGuid());
        var connection = new DelegateWindowsIpcConnection(
            _ => ValueTask.FromResult<IpcFrame?>(response),
            (_, _) => ValueTask.CompletedTask,
            () => ValueTask.CompletedTask);
        var client = CreateClient(
            connection,
            serializer,
            IpcCapabilities.EndpointRpc);

        await Assert.ThrowsExactlyAsync<IpcProtocolException>(() => client.HandshakeAsync());
        Assert.IsFalse(client.IsHandshakeComplete);
        Assert.IsFalse(client.IsConnected);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task OriginalHandshakeFailureIsPreservedWhenConnectionDisposalAlsoFails()
    {
        var readFailure = new IOException("handshake read failed");
        var disposalFailure = new InvalidOperationException("connection disposal failed");
        var connection = new DelegateWindowsIpcConnection(
            _ => ValueTask.FromException<IpcFrame?>(readFailure),
            (_, _) => ValueTask.CompletedTask,
            () => ValueTask.FromException(disposalFailure));
        var client = CreateClient(connection);

        var observed = await Assert.ThrowsAsync<AggregateException>(async () =>
            await client.HandshakeAsync());

        var failures = observed.Flatten().InnerExceptions;
        Assert.IsTrue(failures.Contains(readFailure));
        Assert.IsTrue(failures.Contains(disposalFailure));
        Assert.AreSame(disposalFailure, client.CleanupFailure);
        Assert.IsFalse(failures.Any(failure => failure is ObjectDisposedException));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.DisposeAsync());
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ReadLoopDisposalFailureStillCompletesAndRemovesPendingRequests()
    {
        var serializer = new WindowsIpcSerializer();
        var requestWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReadFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readFailure = new IOException("read loop failed");
        var disposalFailure = new InvalidOperationException("connection disposal failed");
        var readCount = 0;
        var connection = new DelegateWindowsIpcConnection(
            async _ =>
            {
                if (Interlocked.Increment(ref readCount) == 1)
                    return CreateHandshakeResponse(serializer, Guid.NewGuid());

                await releaseReadFailure.Task;
                throw readFailure;
            },
            (frame, _) =>
            {
                if (frame.Header.MessageKind == IpcMessageKind.Request)
                    requestWritten.TrySetResult();
                return ValueTask.CompletedTask;
            },
            () => ValueTask.FromException(disposalFailure));
        var client = CreateClient(connection, serializer);
        await client.HandshakeAsync();
        var pendingTask = client.SendAsync(IpcOperationId.Ping);
        await requestWritten.Task;

        releaseReadFailure.TrySetResult();
        var observed = await Assert.ThrowsAsync<IpcConnectionClosedException>(async () =>
            await pendingTask);

        Assert.AreSame(readFailure, observed.InnerException);
        Assert.AreEqual(0, client.PendingRequestCount);
        Assert.IsNotNull(client.TerminationFailure);
        Assert.AreSame(disposalFailure, client.CleanupFailure);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.DisposeAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.DisposeAsync());
    }

    private static WindowsIpcClient CreateClient(
        IWindowsIpcConnection connection,
        WindowsIpcSerializer? serializer = null,
        IpcCapabilities requiredServerCapabilities = IpcCapabilities.None) =>
        new(
            connection,
            serializer ?? new WindowsIpcSerializer(),
            new WindowsIpcClientOptions(
                IpcPeerRole.Ui,
                IpcPeerRole.Agent,
                IpcCapabilities.Control | IpcCapabilities.Status,
                Environment.ProcessId,
                0,
                Guid.NewGuid(),
                requiredServerCapabilities: requiredServerCapabilities));

    private static IpcFrame CreateHandshakeResponse(
        WindowsIpcSerializer serializer,
        Guid connectionId)
    {
        var response = new IpcHandshakeResponse(
            Accepted: true,
            WindowsIpcProtocol.CurrentVersion,
            IpcPeerRole.Agent,
            connectionId,
            IpcCapabilities.Control | IpcCapabilities.Status,
            Error: null);
        var payload = serializer.Serialize(
            response,
            WindowsIpcJsonContext.Default.IpcHandshakeResponse);
        return new IpcFrame(
            new IpcFrameHeader(
                WindowsIpcProtocol.CurrentVersion,
                IpcMessageKind.HandshakeResponse,
                IpcFrameFlags.None,
                1,
                payload.Length),
            payload);
    }
}
