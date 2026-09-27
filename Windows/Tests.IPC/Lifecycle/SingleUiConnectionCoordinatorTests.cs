using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Lifecycle;

[TestClass]
public sealed class SingleUiConnectionCoordinatorTests
{
    [TestMethod]
    public void SameIdentityMayReplaceBrokenControlConnectionAndStaleUnregisterCannotRemoveIt()
    {
        var coordinator = new SingleUiConnectionCoordinator();
        var instanceId = Guid.NewGuid();
        var first = CreateUiContext(Guid.NewGuid(), 101, 4, instanceId);
        var replacement = CreateUiContext(Guid.NewGuid(), 101, 4, instanceId);

        Assert.IsTrue(coordinator.TryRegister(first, out var firstRegistration));
        Assert.IsTrue(coordinator.TryRegister(replacement, out var replacementRegistration));
        Assert.AreNotEqual(firstRegistration.ConnectionId, replacementRegistration.ConnectionId);
        Assert.IsTrue(replacementRegistration.Generation > firstRegistration.Generation);
        Assert.IsFalse(coordinator.Unregister(first.ConnectionId));
        Assert.AreEqual(replacement.ConnectionId, coordinator.RegisteredConnectionId);
        Assert.IsTrue(coordinator.IsCurrentRegistration(101, 4, instanceId, replacementRegistration.Generation));
    }

    [TestMethod]
    public void RegistrationChangesExposeReplacementAndNormalUnregister()
    {
        var coordinator = new SingleUiConnectionCoordinator();
        var changes = new List<UiConnectionRegistrationChangedEventArgs>();
        coordinator.RegistrationChanged += (_, args) => changes.Add(args);
        var instanceId = Guid.NewGuid();
        var first = CreateUiContext(Guid.NewGuid(), 101, 4, instanceId);
        var replacement = CreateUiContext(Guid.NewGuid(), 101, 4, instanceId);

        Assert.IsTrue(coordinator.TryRegister(first, out var firstRegistration));
        Assert.IsTrue(coordinator.TryRegister(replacement, out var replacementRegistration));
        Assert.IsTrue(coordinator.Unregister(replacement.ConnectionId));

        Assert.AreEqual(3, changes.Count);
        Assert.IsNull(changes[0].Previous);
        Assert.AreEqual(firstRegistration, changes[0].Current);
        Assert.AreEqual(firstRegistration, changes[1].Previous);
        Assert.AreEqual(replacementRegistration, changes[1].Current);
        Assert.AreEqual(replacementRegistration, changes[2].Previous);
        Assert.IsNull(changes[2].Current);
    }

    [TestMethod]
    public void DifferentIdentityIsRejectedUntilCurrentConnectionUnregisters()
    {
        var coordinator = new SingleUiConnectionCoordinator();
        var first = CreateUiContext(Guid.NewGuid(), 101, 4, Guid.NewGuid());
        var other = CreateUiContext(Guid.NewGuid(), 102, 4, Guid.NewGuid());

        Assert.IsTrue(coordinator.TryRegister(first, out _));
        Assert.IsFalse(coordinator.TryRegister(other, out _));
        Assert.IsTrue(coordinator.Unregister(first.ConnectionId));
        Assert.IsTrue(coordinator.TryRegister(other, out _));
        Assert.AreEqual(other.ConnectionId, coordinator.RegisteredConnectionId);
    }

    [TestMethod]
    public void ServerAcceptingUiClientsRequiresCoordinator()
    {
        var pair = new InMemoryIpcConnectionPair();

        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new WindowsIpcServerConnectionSession(
                pair.Server,
                new WindowsIpcSerializer(),
                new WindowsIpcRequestDispatcher(Array.Empty<IWindowsIpcRequestHandler>()),
                new WindowsIpcServerOptions(
                    IpcPeerRole.Agent,
                    new[] { IpcPeerRole.Ui },
                    IpcCapabilities.Control)));
    }

    [TestMethod]
    public void InvalidRegistrationInputsAreRejected()
    {
        var coordinator = new SingleUiConnectionCoordinator();
        var nonUi = new IpcConnectionContext(
            Guid.NewGuid(),
            IpcPeerRole.TestClient,
            100,
            0,
            Guid.NewGuid(),
            IpcCapabilities.Control);

        Assert.ThrowsExactly<ArgumentNullException>(() =>
            coordinator.TryRegister(null!, out _));
        Assert.ThrowsExactly<ArgumentException>(() =>
            coordinator.TryRegister(nonUi, out _));
        Assert.ThrowsExactly<ArgumentException>(() => coordinator.Unregister(Guid.Empty));
    }


    [TestMethod]
    public void IntentionalShutdownCoordinationBlocksReplacementUntilCancelled()
    {
        var coordinator = new SingleUiConnectionCoordinator();
        var instanceId = Guid.NewGuid();
        var first = CreateUiContext(Guid.NewGuid(), 101, 4, instanceId);
        var replacement = CreateUiContext(Guid.NewGuid(), 101, 4, instanceId);
        Assert.IsTrue(coordinator.TryRegister(first, out var registration));

        Assert.IsTrue(coordinator.TryBeginIntentionalShutdown(out var coordinated));
        Assert.AreEqual(registration, coordinated);
        Assert.IsFalse(coordinator.TryRegister(replacement, out _));

        coordinator.CancelIntentionalShutdown();
        Assert.IsTrue(coordinator.TryRegister(replacement, out _));
    }

    [TestMethod]
    public void NoUiShutdownCoordinationAtomicallyBlocksLateRegistration()
    {
        var coordinator = new SingleUiConnectionCoordinator();

        Assert.IsTrue(coordinator.TryBeginIntentionalShutdown(out var registration));
        Assert.IsNull(registration);
        Assert.IsFalse(coordinator.TryRegister(
            CreateUiContext(Guid.NewGuid(), 101, 4, Guid.NewGuid()),
            out _));
    }

    private static IpcConnectionContext CreateUiContext(
        Guid connectionId,
        int processId,
        int windowsSessionId,
        Guid instanceId) => new(
            connectionId,
            IpcPeerRole.Ui,
            processId,
            windowsSessionId,
            instanceId,
            IpcCapabilities.Control | IpcCapabilities.Status | IpcCapabilities.EndpointRpc);
}
