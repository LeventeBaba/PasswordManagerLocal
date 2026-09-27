using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Agent.Endpoint;
using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;

namespace PasswordManagerLocal.Windows.Tests.IPC.Agent;

[TestClass]
public sealed class RegisteredUiEndpointRegistrationResolverTests
{
    [TestMethod]
    public void CurrentControlRegistrationResolvesExactSharedIdentity()
    {
        var coordinator = new SingleUiConnectionCoordinator();
        var instanceId = Guid.NewGuid();
        Assert.IsTrue(coordinator.TryRegister(
            CreateContext(Guid.NewGuid(), 100, 3, instanceId),
            out var registration));
        using var resolver = new RegisteredUiEndpointRegistrationResolver(
            coordinator,
            CreateOpenAdmissionGate());

        Assert.IsTrue(resolver.TryResolve(100, 3, instanceId, out var generation));
        Assert.AreEqual(registration.Generation, generation);
        Assert.IsTrue(resolver.IsCurrent(100, 3, instanceId, generation));
    }

    [TestMethod]
    public void DifferentProcessSessionOrInstanceCannotResolve()
    {
        var coordinator = new SingleUiConnectionCoordinator();
        var instanceId = Guid.NewGuid();
        Assert.IsTrue(coordinator.TryRegister(
            CreateContext(Guid.NewGuid(), 100, 3, instanceId),
            out _));
        using var resolver = new RegisteredUiEndpointRegistrationResolver(
            coordinator,
            CreateOpenAdmissionGate());

        Assert.IsFalse(resolver.TryResolve(101, 3, instanceId, out _));
        Assert.IsFalse(resolver.TryResolve(100, 4, instanceId, out _));
        Assert.IsFalse(resolver.TryResolve(100, 3, Guid.NewGuid(), out _));
    }

    [TestMethod]
    public void ReplacedControlConnectionInvalidatesOldRegistrationGeneration()
    {
        var coordinator = new SingleUiConnectionCoordinator();
        var instanceId = Guid.NewGuid();
        Assert.IsTrue(coordinator.TryRegister(
            CreateContext(Guid.NewGuid(), 100, 3, instanceId),
            out var first));
        Assert.IsTrue(coordinator.TryRegister(
            CreateContext(Guid.NewGuid(), 100, 3, instanceId),
            out var replacement));
        using var resolver = new RegisteredUiEndpointRegistrationResolver(
            coordinator,
            CreateOpenAdmissionGate());

        Assert.IsFalse(resolver.IsCurrent(100, 3, instanceId, first.Generation));
        Assert.IsTrue(resolver.IsCurrent(100, 3, instanceId, replacement.Generation));
    }

    [TestMethod]
    public void ClosedAdmissionInvalidatesCurrentRegistrationForEndpointAuthorization()
    {
        var coordinator = new SingleUiConnectionCoordinator();
        var instanceId = Guid.NewGuid();
        Assert.IsTrue(coordinator.TryRegister(
            CreateContext(Guid.NewGuid(), 100, 3, instanceId),
            out var registration));
        var gate = CreateOpenAdmissionGate();
        using var resolver = new RegisteredUiEndpointRegistrationResolver(coordinator, gate);

        Assert.IsTrue(resolver.TryResolve(100, 3, instanceId, out var generation));
        Assert.AreEqual(registration.Generation, generation);

        gate.ClosePermanently();

        Assert.IsFalse(resolver.TryResolve(100, 3, instanceId, out _));
        Assert.IsFalse(resolver.IsCurrent(100, 3, instanceId, registration.Generation));
    }

    [TestMethod]
    public void ResolverForwardsRegistrationReplacementForEndpointInvalidation()
    {
        var coordinator = new SingleUiConnectionCoordinator();
        using var resolver = new RegisteredUiEndpointRegistrationResolver(
            coordinator,
            CreateOpenAdmissionGate());
        UiConnectionRegistrationChangedEventArgs? observed = null;
        resolver.RegistrationChanged += (_, args) => observed = args;
        var instanceId = Guid.NewGuid();
        Assert.IsTrue(coordinator.TryRegister(
            CreateContext(Guid.NewGuid(), 100, 3, instanceId),
            out var first));
        observed = null;

        Assert.IsTrue(coordinator.TryRegister(
            CreateContext(Guid.NewGuid(), 100, 3, instanceId),
            out var replacement));

        Assert.IsNotNull(observed);
        Assert.AreEqual(first, observed.Previous);
        Assert.AreEqual(replacement, observed.Current);
    }

    private static IpcConnectionContext CreateContext(
        Guid connectionId,
        int processId,
        int windowsSessionId,
        Guid instanceId) => new(
            connectionId,
            IpcPeerRole.Ui,
            processId,
            windowsSessionId,
            instanceId,
            IpcCapabilities.Control | IpcCapabilities.Status);

    private static WindowsAgentAdmissionGate CreateOpenAdmissionGate()
    {
        var gate = new WindowsAgentAdmissionGate();
        gate.Open();
        return gate;
    }
}
