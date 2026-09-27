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
public sealed class WindowsIpcRequestLimitTests
{
    [TestMethod]
    [Timeout(10_000)]
    public async Task ClientWaitsAtLimitAndReturnsCapacityAfterSuccess()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.Ping,
            async (context, cancellationToken) =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }

                return context.Success();
            });
        await using var session = await IpcTestSession.CreateAsync(
            new[] { handler },
            maximumPendingRequests: 1);
        var first = session.Client.SendAsync(IpcOperationId.Ping);
        await firstStarted.Task;

        var second = session.Client.SendAsync(IpcOperationId.Ping);
        await Task.Yield();
        Assert.IsFalse(second.IsCompleted);
        Assert.AreEqual(1, session.Client.PendingRequestCount);

        releaseFirst.TrySetResult();
        Assert.IsTrue((await first).IsSuccess);
        Assert.IsTrue((await second).IsSuccess);
        Assert.AreEqual(0, session.Client.PendingRequestCount);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ClientReturnsCapacityAfterCancellation()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.Ping,
            async (context, cancellationToken) =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    firstStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return context.Success();
            });
        await using var session = await IpcTestSession.CreateAsync(
            new[] { handler },
            maximumPendingRequests: 1);
        using var cancellationSource = new CancellationTokenSource();
        var first = session.Client.SendAsync(
            IpcOperationId.Ping,
            cancellationToken: cancellationSource.Token);
        await firstStarted.Task;

        cancellationSource.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await first);
        Assert.AreEqual(0, session.Client.PendingRequestCount);
        Assert.IsTrue((await session.Client.SendAsync(IpcOperationId.Ping)).IsSuccess);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ClientReturnsCapacityAfterSendFailure()
    {
        var serializer = new WindowsIpcSerializer();
        var responseReady = new TaskCompletionSource<IpcFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCount = 0;
        var requestWriteCount = 0;
        var connection = new DelegateWindowsIpcConnection(
            async cancellationToken =>
            {
                if (Interlocked.Increment(ref readCount) == 1)
                    return CreateHandshakeResponse(serializer);
                if (readCount == 2)
                    return await responseReady.Task.WaitAsync(cancellationToken);

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            },
            (frame, _) =>
            {
                if (frame.Header.MessageKind != IpcMessageKind.Request)
                    return ValueTask.CompletedTask;

                if (Interlocked.Increment(ref requestWriteCount) == 1)
                    return ValueTask.FromException(new IOException("request write failed"));

                var envelope = IpcResponseEnvelope.Success(frame.Header.CorrelationId);
                var payload = serializer.Serialize(
                    envelope,
                    WindowsIpcJsonContext.Default.IpcResponseEnvelope);
                responseReady.TrySetResult(new IpcFrame(
                    new IpcFrameHeader(
                        WindowsIpcProtocol.CurrentVersion,
                        IpcMessageKind.Response,
                        IpcFrameFlags.None,
                        frame.Header.CorrelationId,
                        payload.Length),
                    payload));
                return ValueTask.CompletedTask;
            },
            () => ValueTask.CompletedTask);
        var client = new WindowsIpcClient(
            connection,
            serializer,
            new WindowsIpcClientOptions(
                IpcPeerRole.Ui,
                IpcPeerRole.Agent,
                IpcCapabilities.Control,
                Environment.ProcessId,
                0,
                Guid.NewGuid(),
                maximumPendingRequests: 1));
        await client.HandshakeAsync();

        await Assert.ThrowsAsync<IOException>(async () =>
            await client.SendAsync(IpcOperationId.Ping));
        Assert.AreEqual(0, client.PendingRequestCount);
        Assert.IsTrue((await client.SendAsync(IpcOperationId.Ping)).IsSuccess);

        await client.DisposeAsync();
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ServerReturnsBusyAboveLimitAndConnectionRemainsUsable()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.Ping,
            async (context, cancellationToken) =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }

                return context.Success();
            });
        await using var session = await IpcTestSession.CreateAsync(
            new[] { handler },
            maximumPendingRequests: 4,
            maximumActiveRequestsPerConnection: 1);
        var first = session.Client.SendAsync(IpcOperationId.Ping);
        await firstStarted.Task;

        var busy = await Assert.ThrowsAsync<IpcRemoteException>(async () =>
            await session.Client.SendAsync(IpcOperationId.Ping));
        Assert.AreEqual(IpcErrorCode.TooManyRequests, busy.Error.ErrorCode);
        Assert.AreEqual(IpcErrorCategory.Availability, busy.Error.ErrorCategory);
        Assert.IsTrue(busy.Error.IsRetryable);
        Assert.IsFalse(busy.Error.RequiresProcessRestart);
        Assert.IsTrue(busy.Error.CorrelationId > 0);
        Assert.AreEqual(1, session.Server.ActiveRequestCount);

        releaseFirst.TrySetResult();
        Assert.IsTrue((await first).IsSuccess);
        Assert.IsTrue((await session.Client.SendAsync(IpcOperationId.Ping)).IsSuccess);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ServerReturnsCapacityAfterHandlerFailure()
    {
        var callCount = 0;
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.Ping,
            (context, _) =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    throw new InvalidOperationException("handler failed");

                return Task.FromResult(context.Success());
            });
        await using var session = await IpcTestSession.CreateAsync(
            new[] { handler },
            maximumPendingRequests: 1,
            maximumActiveRequestsPerConnection: 1);

        var failure = await Assert.ThrowsAsync<IpcRemoteException>(async () =>
            await session.Client.SendAsync(IpcOperationId.Ping));
        Assert.AreEqual(IpcErrorCode.HandlerFailed, failure.Error.ErrorCode);
        Assert.IsTrue((await session.Client.SendAsync(IpcOperationId.Ping)).IsSuccess);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task CancellationRemainsProcessableAtServerRequestLimit()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.Ping,
            async (context, cancellationToken) =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    firstStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    finally
                    {
                        firstCancelled.TrySetResult();
                    }
                }

                return context.Success();
            });
        await using var session = await IpcTestSession.CreateAsync(
            new[] { handler },
            maximumActiveRequestsPerConnection: 1);
        using var cancellationSource = new CancellationTokenSource();
        var first = session.Client.SendAsync(
            IpcOperationId.Ping,
            cancellationToken: cancellationSource.Token);
        await firstStarted.Task;

        cancellationSource.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await first);
        await firstCancelled.Task;
        Assert.IsTrue((await session.Client.SendAsync(IpcOperationId.Ping)).IsSuccess);
    }

    [TestMethod]
    public void ClientRequestLimitBoundariesAreEnforced()
    {
        var minimum = new WindowsIpcClientOptions(
            IpcPeerRole.Ui,
            IpcPeerRole.Agent,
            IpcCapabilities.Control,
            Environment.ProcessId,
            0,
            Guid.NewGuid(),
            maximumPendingRequests: 1);
        var maximum = new WindowsIpcClientOptions(
            IpcPeerRole.Ui,
            IpcPeerRole.Agent,
            IpcCapabilities.Control,
            Environment.ProcessId,
            0,
            Guid.NewGuid(),
            WindowsIpcClientOptions.MaximumConfigurablePendingRequests);
        var defaults = new WindowsIpcClientOptions(
            IpcPeerRole.Ui,
            IpcPeerRole.Agent,
            IpcCapabilities.Control,
            Environment.ProcessId,
            0,
            Guid.NewGuid());

        Assert.AreEqual(1, minimum.MaximumPendingRequests);
        Assert.AreEqual(
            WindowsIpcClientOptions.MaximumConfigurablePendingRequests,
            maximum.MaximumPendingRequests);
        Assert.AreEqual(
            WindowsIpcClientOptions.DefaultMaximumPendingRequests,
            defaults.MaximumPendingRequests);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new WindowsIpcClientOptions(
                IpcPeerRole.Ui,
                IpcPeerRole.Agent,
                IpcCapabilities.Control,
                Environment.ProcessId,
                0,
                Guid.NewGuid(),
                maximumPendingRequests: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new WindowsIpcClientOptions(
                IpcPeerRole.Ui,
                IpcPeerRole.Agent,
                IpcCapabilities.Control,
                Environment.ProcessId,
                0,
                Guid.NewGuid(),
                maximumPendingRequests: -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new WindowsIpcClientOptions(
                IpcPeerRole.Ui,
                IpcPeerRole.Agent,
                IpcCapabilities.Control,
                Environment.ProcessId,
                0,
                Guid.NewGuid(),
                WindowsIpcClientOptions.MaximumConfigurablePendingRequests + 1));
    }

    [TestMethod]
    public void ServerRequestLimitBoundariesAreEnforced()
    {
        var minimum = new WindowsIpcServerOptions(
            IpcPeerRole.Agent,
            new[] { IpcPeerRole.Ui },
            IpcCapabilities.Control,
            maximumActiveRequestsPerConnection: 1);
        var maximum = new WindowsIpcServerOptions(
            IpcPeerRole.Agent,
            new[] { IpcPeerRole.Ui },
            IpcCapabilities.Control,
            WindowsIpcServerOptions.MaximumConfigurableActiveRequestsPerConnection);
        var defaults = new WindowsIpcServerOptions(
            IpcPeerRole.Agent,
            new[] { IpcPeerRole.Ui },
            IpcCapabilities.Control);

        Assert.AreEqual(1, minimum.MaximumActiveRequestsPerConnection);
        Assert.AreEqual(
            WindowsIpcServerOptions.MaximumConfigurableActiveRequestsPerConnection,
            maximum.MaximumActiveRequestsPerConnection);
        Assert.AreEqual(
            WindowsIpcServerOptions.DefaultMaximumActiveRequestsPerConnection,
            defaults.MaximumActiveRequestsPerConnection);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new WindowsIpcServerOptions(
                IpcPeerRole.Agent,
                new[] { IpcPeerRole.Ui },
                IpcCapabilities.Control,
                maximumActiveRequestsPerConnection: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new WindowsIpcServerOptions(
                IpcPeerRole.Agent,
                new[] { IpcPeerRole.Ui },
                IpcCapabilities.Control,
                maximumActiveRequestsPerConnection: -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new WindowsIpcServerOptions(
                IpcPeerRole.Agent,
                new[] { IpcPeerRole.Ui },
                IpcCapabilities.Control,
                WindowsIpcServerOptions.MaximumConfigurableActiveRequestsPerConnection + 1));
    }

    private static IpcFrame CreateHandshakeResponse(WindowsIpcSerializer serializer)
    {
        var response = new IpcHandshakeResponse(
            Accepted: true,
            WindowsIpcProtocol.CurrentVersion,
            IpcPeerRole.Agent,
            Guid.NewGuid(),
            IpcCapabilities.Control,
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
