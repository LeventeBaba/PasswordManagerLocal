using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Lifecycle;

[TestClass]
public sealed class WindowsIpcConnectionLifecycleTests
{
    [TestMethod]
    public async Task ConnectionReportsHandshakeCompletionAndCleanDisconnect()
    {
        var observer = new RecordingLifecycleObserver();
        var session = await IpcTestSession.CreateAsync(
            new IWindowsIpcRequestHandler[] { new PingWindowsIpcRequestHandler() },
            observers: new[] { observer });

        await session.DisposeAsync();

        var states = observer.Notifications.Select(item => item.State).ToArray();
        CollectionAssert.Contains(states, IpcConnectionLifecycleState.Connected);
        CollectionAssert.Contains(states, IpcConnectionLifecycleState.HandshakeCompleted);
        CollectionAssert.Contains(states, IpcConnectionLifecycleState.Disconnected);
        Assert.AreEqual(
            IpcDisconnectKind.Clean,
            observer.Notifications.Last().DisconnectKind);
    }

    [TestMethod]
    public async Task FaultedDisconnectIsReported()
    {
        var observer = new RecordingLifecycleObserver();
        var session = await IpcTestSession.CreateAsync(
            new IWindowsIpcRequestHandler[] { new PingWindowsIpcRequestHandler() },
            observers: new[] { observer });

        session.Pair.Client.Fault(new IOException("transport fault"));
        await session.ServerTask;

        Assert.AreEqual(
            IpcConnectionLifecycleState.Faulted,
            observer.Notifications.Last().State);
        Assert.AreEqual(
            IpcDisconnectKind.TransportFailure,
            observer.Notifications.Last().DisconnectKind);
        await session.Client.DisposeAsync();
    }

    [TestMethod]
    public async Task OnlyOneUiRegistrationIsAcceptedAndDisconnectReleasesIt()
    {
        var coordinator = new SingleUiConnectionCoordinator();
        var handlers = new IWindowsIpcRequestHandler[]
        {
            new RegisterUiConnectionWindowsIpcRequestHandler(coordinator),
            new UnregisterUiConnectionWindowsIpcRequestHandler(coordinator)
        };
        var first = await IpcTestSession.CreateAsync(
            handlers,
            IpcPeerRole.Ui,
            uiConnectionCoordinator: coordinator);
        var second = await IpcTestSession.CreateAsync(
            handlers,
            IpcPeerRole.Ui,
            uiConnectionCoordinator: coordinator);
        var firstControl = new WindowsIpcControlClient(first.Client, first.Serializer);
        var secondControl = new WindowsIpcControlClient(second.Client, second.Serializer);

        var registration = await firstControl.RegisterUiConnectionAsync();
        var exception = await Assert.ThrowsAsync<IpcRemoteException>(async () =>
            await secondControl.RegisterUiConnectionAsync());

        Assert.IsTrue(registration.IsRegistered);
        Assert.AreEqual(PasswordManagerLocal.Windows.Ipc.Contracts.IpcErrorCode.UiAlreadyRegistered, exception.Error.ErrorCode);

        await first.DisposeAsync();
        Assert.IsNull(coordinator.RegisteredConnectionId);

        var secondRegistration = await secondControl.RegisterUiConnectionAsync();
        Assert.IsTrue(secondRegistration.IsRegistered);
        await second.DisposeAsync();
    }
}
