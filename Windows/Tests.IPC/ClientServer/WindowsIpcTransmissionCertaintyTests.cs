using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using PasswordManagerLocal.Windows.Ipc.Validation;

namespace PasswordManagerLocal.Windows.Tests.IPC.ClientServer;

[TestClass]
public sealed class WindowsIpcTransmissionCertaintyTests
{
    [TestMethod]
    public async Task LocalSerializationFailureIsDefinitelyNotSent()
    {
        var serializer = new WindowsIpcSerializer();
        var connection = new RecordingWindowsIpcConnection(serializer);
        var client = CreateClient(
            connection,
            serializer,
            maximumPendingRequests: 1,
            requestEnvelopeSerializer: _ => throw new IOException("serialization failed"));
        await client.HandshakeAsync();

        var exception = await Assert.ThrowsExactlyAsync<IpcRequestTransmissionException>(() =>
            client.SendWithTransmissionStateAsync(IpcOperationId.Ping));

        Assert.AreEqual(IpcRequestTransmissionState.DefinitelyNotSent, exception.TransmissionState);
        Assert.AreEqual(0, connection.CompletedWrites.Count(frame =>
            frame.Header.MessageKind == IpcMessageKind.Request));
        await client.DisposeAsync();
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task CancellationWhileWaitingForPendingCapacityIsDefinitelyNotSent()
    {
        var serializer = new WindowsIpcSerializer();
        var connection = new RecordingWindowsIpcConnection(serializer, blockRequestWrites: true);
        var client = CreateClient(connection, serializer, maximumPendingRequests: 1);
        await client.HandshakeAsync();
        var first = client.SendWithTransmissionStateAsync(IpcOperationId.Ping);
        await connection.WaitForStartedWritesAsync(2);
        using var cancellationSource = new CancellationTokenSource();
        var cancelled = client.SendWithTransmissionStateAsync(
            IpcOperationId.Ping,
            cancellationToken: cancellationSource.Token);

        cancellationSource.Cancel();
        var exception = await Assert.ThrowsExactlyAsync<IpcRequestTransmissionException>(() => cancelled);
        Assert.AreEqual(IpcRequestTransmissionState.DefinitelyNotSent, exception.TransmissionState);

        connection.ReleaseRequestWrites();
        await connection.WaitForCompletedWritesAsync(2);
        var request = connection.CompletedWrites.Single(frame =>
            frame.Header.MessageKind == IpcMessageKind.Request);
        connection.QueueSuccessResponse(request.Header.CorrelationId);
        Assert.AreEqual(IpcRequestTransmissionState.Sent, (await first).TransmissionState);
        await client.DisposeAsync();
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task CancellationWhileQueuedForWriteAdmissionIsDefinitelyNotSent()
    {
        var serializer = new WindowsIpcSerializer();
        var connection = new RecordingWindowsIpcConnection(serializer, blockRequestWrites: true);
        var client = CreateClient(connection, serializer, maximumPendingRequests: 2);
        await client.HandshakeAsync();
        var first = client.SendWithTransmissionStateAsync(IpcOperationId.Ping);
        await connection.WaitForStartedWritesAsync(2);
        using var cancellationSource = new CancellationTokenSource();
        var queued = client.SendWithTransmissionStateAsync(
            IpcOperationId.Ping,
            cancellationToken: cancellationSource.Token);
        await WaitForPendingCountAsync(client, 2);

        cancellationSource.Cancel();
        var exception = await Assert.ThrowsExactlyAsync<IpcRequestTransmissionException>(() => queued);
        Assert.AreEqual(IpcRequestTransmissionState.DefinitelyNotSent, exception.TransmissionState);

        connection.ReleaseRequestWrites();
        await connection.WaitForCompletedWritesAsync(2);
        var request = connection.CompletedWrites.Single(frame =>
            frame.Header.MessageKind == IpcMessageKind.Request);
        connection.QueueSuccessResponse(request.Header.CorrelationId);
        await first;
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task FailureAfterWriteAdmissionIsTransmissionUnknown()
    {
        var serializer = new WindowsIpcSerializer();
        var connection = new RecordingWindowsIpcConnection(serializer)
        {
            FailRequestWrites = true
        };
        var client = CreateClient(connection, serializer, maximumPendingRequests: 1);
        await client.HandshakeAsync();

        var exception = await Assert.ThrowsExactlyAsync<IpcRequestTransmissionException>(() =>
            client.SendWithTransmissionStateAsync(IpcOperationId.Ping));

        Assert.AreEqual(IpcRequestTransmissionState.TransmissionUnknown, exception.TransmissionState);
        Assert.AreEqual(1, connection.AdmissionAttemptCount);
        await client.DisposeAsync();
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task CompleteFrameWriteWithLostResponseIsSent()
    {
        var serializer = new WindowsIpcSerializer();
        var connection = new RecordingWindowsIpcConnection(serializer);
        var client = CreateClient(connection, serializer, maximumPendingRequests: 1);
        await client.HandshakeAsync();
        var send = client.SendWithTransmissionStateAsync(IpcOperationId.Ping);
        await connection.WaitForCompletedWritesAsync(2);

        await connection.DisposeAsync();
        var exception = await Assert.ThrowsExactlyAsync<IpcRequestTransmissionException>(() => send);

        Assert.AreEqual(IpcRequestTransmissionState.Sent, exception.TransmissionState);
        await client.DisposeAsync();
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ConclusiveResponseReportsSent()
    {
        var serializer = new WindowsIpcSerializer();
        var connection = new RecordingWindowsIpcConnection(serializer);
        var client = CreateClient(connection, serializer, maximumPendingRequests: 1);
        await client.HandshakeAsync();
        var send = client.SendWithTransmissionStateAsync(IpcOperationId.Ping);
        await connection.WaitForCompletedWritesAsync(2);
        var request = connection.CompletedWrites.Single(frame =>
            frame.Header.MessageKind == IpcMessageKind.Request);
        connection.QueueSuccessResponse(request.Header.CorrelationId);

        var response = await send;

        Assert.AreEqual(request.Header.CorrelationId, response.CorrelationId);
        Assert.AreEqual(IpcRequestTransmissionState.Sent, response.TransmissionState);
        Assert.IsTrue(response.Response.IsSuccess);
        await client.DisposeAsync();
    }


    [TestMethod]
    [Timeout(10_000)]
    public async Task BackgroundSettingControlWritePreservesSentCancellationState()
    {
        var serializer = new WindowsIpcSerializer();
        var connection = new RecordingWindowsIpcConnection(serializer);
        var client = CreateClient(connection, serializer, maximumPendingRequests: 1);
        await client.HandshakeAsync();
        var controlClient = new WindowsIpcControlClient(client, serializer);
        using var cancellation = new CancellationTokenSource();
        var write = controlClient.SetBackgroundSyncEnabledAsync(
            new SetBackgroundSyncEnabledRequestDto(true),
            cancellation.Token);
        await connection.WaitForCompletedWritesAsync(2);

        cancellation.Cancel();
        var exception = await Assert.ThrowsExactlyAsync<IpcRequestTransmissionException>(() => write);

        Assert.AreEqual(IpcRequestTransmissionState.Sent, exception.TransmissionState);
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task BackgroundSettingControlWritePreservesUnknownTransmissionState()
    {
        var serializer = new WindowsIpcSerializer();
        var connection = new RecordingWindowsIpcConnection(serializer)
        {
            FailRequestWrites = true
        };
        var client = CreateClient(connection, serializer, maximumPendingRequests: 1);
        await client.HandshakeAsync();
        var controlClient = new WindowsIpcControlClient(client, serializer);

        var exception = await Assert.ThrowsExactlyAsync<IpcRequestTransmissionException>(() =>
            controlClient.SetBackgroundSyncEnabledAsync(
                new SetBackgroundSyncEnabledRequestDto(true)));

        Assert.AreEqual(
            IpcRequestTransmissionState.TransmissionUnknown,
            exception.TransmissionState);
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task DisposedClientBeforeTransmissionIsDefinitelyNotSent()
    {
        var serializer = new WindowsIpcSerializer();
        var connection = new RecordingWindowsIpcConnection(serializer);
        var client = CreateClient(connection, serializer, maximumPendingRequests: 1);
        await client.HandshakeAsync();
        await client.DisposeAsync();

        var exception = await Assert.ThrowsExactlyAsync<IpcRequestTransmissionException>(() =>
            client.SendWithTransmissionStateAsync(IpcOperationId.Ping));

        Assert.AreEqual(0, exception.CorrelationId);
        Assert.AreEqual(IpcRequestTransmissionState.DefinitelyNotSent, exception.TransmissionState);
    }

    private static async Task WaitForPendingCountAsync(WindowsIpcClient client, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (client.PendingRequestCount != count)
            await Task.Delay(10, timeout.Token);
    }

    private static WindowsIpcClient CreateClient(
        RecordingWindowsIpcConnection connection,
        WindowsIpcSerializer serializer,
        int maximumPendingRequests,
        Func<IpcRequestEnvelope, byte[]>? requestEnvelopeSerializer = null) =>
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
            pendingRequestRegistrationGuard: null);
}
