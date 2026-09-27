using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using PasswordManagerLocal.Windows.Ipc.Validation;

namespace PasswordManagerLocal.Windows.Tests.IPC.ClientServer;

[TestClass]
public sealed class WindowsIpcRequestCapacityAdmissionTests
{
    [TestMethod]
    [Timeout(10_000)]
    public async Task SerializationStartsOnlyAfterRequestCapacityIsAvailable()
    {
        var serializer = new WindowsIpcSerializer();
        var connection = new RecordingWindowsIpcConnection(
            serializer,
            blockRequestWrites: true);
        var serializationCount = 0;
        var client = CreateClient(
            connection,
            serializer,
            maximumPendingRequests: 1,
            requestEnvelopeSerializer: request =>
            {
                Interlocked.Increment(ref serializationCount);
                return SerializeRequest(serializer, request);
            });
        await client.HandshakeAsync();
        var first = client.SendAsync(IpcOperationId.Ping);
        await connection.WaitForStartedWritesAsync(2);

        var second = client.SendAsync(IpcOperationId.Ping);
        await Task.Yield();
        Assert.AreEqual(1, Volatile.Read(ref serializationCount));
        Assert.IsFalse(second.IsCompleted);

        connection.ReleaseRequestWrites();
        await connection.WaitForCompletedWritesAsync(2);
        var firstRequest = connection.CompletedWrites.Single(frame =>
            frame.Header.MessageKind == IpcMessageKind.Request);
        connection.QueueSuccessResponse(firstRequest.Header.CorrelationId);
        Assert.IsTrue((await first).IsSuccess);
        await WaitUntilAsync(() => Volatile.Read(ref serializationCount) == 2);
        await connection.WaitForCompletedWritesAsync(3);
        var secondRequest = connection.CompletedWrites.Last(frame =>
            frame.Header.MessageKind == IpcMessageKind.Request);
        connection.QueueSuccessResponse(secondRequest.Header.CorrelationId);
        Assert.IsTrue((await second).IsSuccess);
        await client.DisposeAsync();
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task CallerCancellationWhileWaitingForCapacityDoesNotSerializeOrLeakPermit()
    {
        var serializer = new WindowsIpcSerializer();
        var connection = new RecordingWindowsIpcConnection(
            serializer,
            blockRequestWrites: true);
        var serializationCount = 0;
        var client = CreateClient(
            connection,
            serializer,
            maximumPendingRequests: 1,
            requestEnvelopeSerializer: request =>
            {
                Interlocked.Increment(ref serializationCount);
                return SerializeRequest(serializer, request);
            });
        await client.HandshakeAsync();
        var first = client.SendAsync(IpcOperationId.Ping);
        await connection.WaitForStartedWritesAsync(2);
        using var cancellationSource = new CancellationTokenSource();
        var cancelled = client.SendAsync(
            IpcOperationId.Ping,
            cancellationToken: cancellationSource.Token);

        cancellationSource.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelled);
        Assert.AreEqual(1, Volatile.Read(ref serializationCount));

        connection.ReleaseRequestWrites();
        await connection.WaitForCompletedWritesAsync(2);
        var firstRequest = connection.CompletedWrites.Single(frame =>
            frame.Header.MessageKind == IpcMessageKind.Request);
        connection.QueueSuccessResponse(firstRequest.Header.CorrelationId);
        Assert.IsTrue((await first).IsSuccess);

        var followUp = client.SendAsync(IpcOperationId.Ping);
        await connection.WaitForCompletedWritesAsync(3);
        var followUpRequest = connection.CompletedWrites.Last(frame =>
            frame.Header.MessageKind == IpcMessageKind.Request);
        connection.QueueSuccessResponse(followUpRequest.Header.CorrelationId);
        Assert.IsTrue((await followUp).IsSuccess);
        Assert.AreEqual(2, Volatile.Read(ref serializationCount));
        await client.DisposeAsync();
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task CallerCancellationDuringSerializationPreventsRegistrationAndReleasesCapacity()
    {
        var serializer = new WindowsIpcSerializer();
        var connection = new RecordingWindowsIpcConnection(serializer);
        var serializationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSerialization = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var serializationCount = 0;
        var client = CreateClient(
            connection,
            serializer,
            maximumPendingRequests: 1,
            requestEnvelopeSerializer: request =>
            {
                if (Interlocked.Increment(ref serializationCount) == 1)
                {
                    serializationStarted.TrySetResult();
                    releaseSerialization.Task.GetAwaiter().GetResult();
                }

                return SerializeRequest(serializer, request);
            });
        await client.HandshakeAsync();
        using var cancellationSource = new CancellationTokenSource();
        var cancelled = Task.Run(async () => await client.SendAsync(
            IpcOperationId.Ping,
            cancellationToken: cancellationSource.Token));
        await serializationStarted.Task;

        cancellationSource.Cancel();
        releaseSerialization.TrySetResult();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelled);
        Assert.AreEqual(0, client.PendingRequestCount);
        Assert.AreEqual(0, connection.CompletedWrites.Count(frame =>
            frame.Header.MessageKind == IpcMessageKind.Request));

        var followUp = client.SendAsync(IpcOperationId.Ping);
        await connection.WaitForCompletedWritesAsync(2);
        var request = connection.CompletedWrites.Single(frame =>
            frame.Header.MessageKind == IpcMessageKind.Request);
        connection.QueueSuccessResponse(request.Header.CorrelationId);
        Assert.IsTrue((await followUp).IsSuccess);
        Assert.AreEqual(2, Volatile.Read(ref serializationCount));
        await client.DisposeAsync();
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SerializationFailureReleasesCapacityExactlyOnce()
    {
        var serializer = new WindowsIpcSerializer();
        var connection = new RecordingWindowsIpcConnection(serializer);
        var serializationCount = 0;
        var client = CreateClient(
            connection,
            serializer,
            maximumPendingRequests: 1,
            requestEnvelopeSerializer: request =>
            {
                if (Interlocked.Increment(ref serializationCount) == 1)
                    throw new IOException("serialization failed");

                return SerializeRequest(serializer, request);
            });
        await client.HandshakeAsync();

        await Assert.ThrowsAsync<IOException>(async () =>
            await client.SendAsync(IpcOperationId.Ping));
        Assert.AreEqual(0, client.PendingRequestCount);
        Assert.AreEqual(0, connection.CompletedWrites.Count(frame =>
            frame.Header.MessageKind == IpcMessageKind.Request));

        var followUp = client.SendAsync(IpcOperationId.Ping);
        await connection.WaitForCompletedWritesAsync(2);
        var request = connection.CompletedWrites.Single(frame =>
            frame.Header.MessageKind == IpcMessageKind.Request);
        connection.QueueSuccessResponse(request.Header.CorrelationId);
        Assert.IsTrue((await followUp).IsSuccess);
        await client.DisposeAsync();
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SerializedEnvelopeValidationFailureReleasesCapacityExactlyOnce()
    {
        var serializer = new WindowsIpcSerializer();
        var connection = new RecordingWindowsIpcConnection(serializer);
        var serializationCount = 0;
        var client = CreateClient(
            connection,
            serializer,
            maximumPendingRequests: 1,
            requestEnvelopeSerializer: request =>
            {
                if (Interlocked.Increment(ref serializationCount) == 1)
                    return new byte[WindowsIpcProtocol.MaximumPayloadSize + 1];

                return SerializeRequest(serializer, request);
            });
        await client.HandshakeAsync();

        await Assert.ThrowsAsync<IpcPayloadLimitExceededException>(async () =>
            await client.SendAsync(IpcOperationId.Ping));
        Assert.AreEqual(0, client.PendingRequestCount);

        var followUp = client.SendAsync(IpcOperationId.Ping);
        await connection.WaitForCompletedWritesAsync(2);
        var request = connection.CompletedWrites.Single(frame =>
            frame.Header.MessageKind == IpcMessageKind.Request);
        connection.QueueSuccessResponse(request.Header.CorrelationId);
        Assert.IsTrue((await followUp).IsSuccess);
        await client.DisposeAsync();
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ClientDisposalDuringSerializationReleasesPendingOwnership()
    {
        var serializer = new WindowsIpcSerializer();
        var connection = new RecordingWindowsIpcConnection(serializer);
        var serializationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSerialization = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = CreateClient(
            connection,
            serializer,
            maximumPendingRequests: 1,
            requestEnvelopeSerializer: request =>
            {
                serializationStarted.TrySetResult();
                releaseSerialization.Task.GetAwaiter().GetResult();
                return SerializeRequest(serializer, request);
            });
        await client.HandshakeAsync();
        var sendTask = Task.Run(async () => await client.SendAsync(IpcOperationId.Ping));
        await serializationStarted.Task;

        var disposeTask = client.DisposeAsync().AsTask();
        releaseSerialization.TrySetResult();

        await disposeTask;
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await sendTask);
        Assert.AreEqual(0, client.PendingRequestCount);
        Assert.AreEqual(0, connection.CompletedWrites.Count(frame =>
            frame.Header.MessageKind == IpcMessageKind.Request));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task PendingRegistrationFailureReleasesCapacityExactlyOnce()
    {
        var serializer = new WindowsIpcSerializer();
        var connection = new RecordingWindowsIpcConnection(serializer);
        var registrationCount = 0;
        var client = CreateClient(
            connection,
            serializer,
            maximumPendingRequests: 1,
            pendingRequestRegistrationGuard: (_, _) =>
                Interlocked.Increment(ref registrationCount) != 1);
        await client.HandshakeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.SendAsync(IpcOperationId.Ping));
        Assert.AreEqual(0, client.PendingRequestCount);
        Assert.AreEqual(0, connection.CompletedWrites.Count(frame =>
            frame.Header.MessageKind == IpcMessageKind.Request));

        var followUp = client.SendAsync(IpcOperationId.Ping);
        await connection.WaitForCompletedWritesAsync(2);
        var request = connection.CompletedWrites.Single(frame =>
            frame.Header.MessageKind == IpcMessageKind.Request);
        connection.QueueSuccessResponse(request.Header.CorrelationId);
        Assert.IsTrue((await followUp).IsSuccess);
        await client.DisposeAsync();
    }

    private static WindowsIpcClient CreateClient(
        RecordingWindowsIpcConnection connection,
        WindowsIpcSerializer serializer,
        int maximumPendingRequests,
        Func<IpcRequestEnvelope, byte[]>? requestEnvelopeSerializer = null,
        Func<long, PendingIpcRequest, bool>? pendingRequestRegistrationGuard = null) =>
        new(
            connection,
            serializer,
            new WindowsIpcClientOptions(
                IpcPeerRole.Ui,
                IpcPeerRole.Agent,
                IpcCapabilities.Control,
                Environment.ProcessId,
                0,
                Guid.NewGuid(),
                maximumPendingRequests),
            new WindowsIpcContractValidator(),
            requestEnvelopeSerializer,
            pendingRequestRegistrationGuard);

    private static byte[] SerializeRequest(
        WindowsIpcSerializer serializer,
        IpcRequestEnvelope request) =>
        serializer.Serialize(request, WindowsIpcJsonContext.Default.IpcRequestEnvelope);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        while (!condition())
            await Task.Yield();
    }
}
