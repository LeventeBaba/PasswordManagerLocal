using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using PasswordManagerLocal.Windows.Ipc.Transport;

namespace PasswordManagerLocal.Windows.Tests.IPC.ClientServer;

[TestClass]
public sealed class WindowsIpcServerLifecycleHardeningTests
{
    [TestMethod]
    [Timeout(10_000)]
    public async Task DisposalFailureStillDrainsRequestsNotifiesObserversAndUnregistersUi()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new RecordingLifecycleObserver();
        var coordinator = new SingleUiConnectionCoordinator();
        var disposalFailure = new InvalidOperationException("server connection disposal failed");
        var handlers = new IWindowsIpcRequestHandler[]
        {
            new RegisterUiConnectionWindowsIpcRequestHandler(coordinator),
            new DelegateWindowsIpcRequestHandler(
                IpcOperationId.Ping,
                async (context, cancellationToken) =>
                {
                    requestStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    finally
                    {
                        requestExited.TrySetResult();
                    }

                    return context.Success();
                })
        };
        var session = await IpcTestSession.CreateAsync(
            handlers,
            IpcPeerRole.Ui,
            observers: new[] { observer },
            uiConnectionCoordinator: coordinator);
        var controlClient = new WindowsIpcControlClient(session.Client, session.Serializer);
        await controlClient.RegisterUiConnectionAsync();
        Assert.AreEqual(session.Pair.Server.ConnectionId, coordinator.RegisteredConnectionId);

        var pendingRequest = session.Client.SendAsync(IpcOperationId.Ping);
        await requestStarted.Task;
        session.Pair.Server.DisposeException = disposalFailure;
        session.Pair.Client.Fault(new IOException("server read failed"));

        await session.ServerTask;
        await requestExited.Task;
        await Assert.ThrowsAsync<IpcConnectionClosedException>(async () => await pendingRequest);

        Assert.AreEqual(0, session.Server.ActiveRequestCount);
        Assert.IsNull(coordinator.RegisteredConnectionId);
        Assert.AreSame(disposalFailure, session.Server.CleanupFailure);
        Assert.AreEqual(IpcConnectionLifecycleState.Faulted, observer.Notifications.Last().State);
        Assert.AreEqual(IpcDisconnectKind.TransportFailure, observer.Notifications.Last().DisconnectKind);

        await session.Client.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.Server.DisposeAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.Server.DisposeAsync());
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ObserverFailureCannotPreventMandatoryUiUnregistration()
    {
        var coordinator = new SingleUiConnectionCoordinator();
        var observerFailure = new InvalidOperationException("observer failed");
        var disposalFailure = new IOException("connection disposal failed");
        var observer = new DelegateConnectionLifecycleObserver(
            (notification, _) =>
            {
                if (notification.State is IpcConnectionLifecycleState.Disconnected or
                    IpcConnectionLifecycleState.Faulted)
                {
                    return ValueTask.FromException(observerFailure);
                }

                return ValueTask.CompletedTask;
            });
        var handlers = new IWindowsIpcRequestHandler[]
        {
            new RegisterUiConnectionWindowsIpcRequestHandler(coordinator)
        };
        var session = await IpcTestSession.CreateAsync(
            handlers,
            IpcPeerRole.Ui,
            observers: new[] { observer },
            uiConnectionCoordinator: coordinator);
        var controlClient = new WindowsIpcControlClient(session.Client, session.Serializer);
        await controlClient.RegisterUiConnectionAsync();
        session.Pair.Server.DisposeException = disposalFailure;

        session.Pair.Client.Fault(new IOException("transport failed"));
        await session.ServerTask;

        Assert.IsNull(coordinator.RegisteredConnectionId);
        var cleanup = session.Server.CleanupFailure as AggregateException;
        Assert.IsNotNull(cleanup);
        var failures = cleanup.Flatten().InnerExceptions;
        Assert.IsTrue(failures.Contains(observerFailure));
        Assert.IsTrue(failures.Contains(disposalFailure));

        await session.Client.DisposeAsync();
        var observed = await Assert.ThrowsAsync<AggregateException>(async () =>
            await session.Server.DisposeAsync());
        Assert.IsTrue(observed.Flatten().InnerExceptions.Contains(observerFailure));
        Assert.IsTrue(observed.Flatten().InnerExceptions.Contains(disposalFailure));
    }
}
