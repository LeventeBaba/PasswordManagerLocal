using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using PasswordManagerLocal.Windows.Frontend.SingleInstance;

namespace PasswordManagerLocal.Windows.Tests.IPC.SingleInstance;

[TestClass]
public sealed class WindowsUiSingleInstanceControllerTests
{
    [TestMethod]
    public async Task FirstUiOwnerBecomesPrimaryWithoutActivationRequest()
    {
        var processLock = new FakeProcessInstanceLock();
        var activation = new DelegateWindowsUiActivationClient((_, _) =>
            Task.FromResult(new UiActivationResult(UiActivationResultKind.Activated, "Activated.")));
        var controller = new WindowsUiSingleInstanceController(processLock, activation);

        var role = await controller.EnterAsync();

        Assert.AreEqual(WindowsUiInstanceRole.Primary, role);
        Assert.AreEqual(1, processLock.EnsureOwnershipCount);
        Assert.AreEqual(0, activation.CallCount);
    }

    [TestMethod]
    [DataRow(UiActivationResultKind.Activated)]
    [DataRow(UiActivationResultKind.Rejected)]
    public async Task ConclusiveActivationReplyKeepsExistingUiAuthoritative(UiActivationResultKind resultKind)
    {
        var processLock = new FakeProcessInstanceLock(isOwner: false);
        var activation = new DelegateWindowsUiActivationClient((_, _) =>
            Task.FromResult(new UiActivationResult(resultKind, "Conclusive activation reply.")));
        var controller = new WindowsUiSingleInstanceController(
            processLock, activation, maximumActivationAttempts: 3, retryDelay: TimeSpan.Zero);

        var role = await controller.EnterAsync();

        Assert.AreEqual(WindowsUiInstanceRole.SecondaryActivationRequested, role);
        Assert.AreEqual(1, activation.CallCount);
        Assert.IsFalse(processLock.IsOwner);
    }

    [TestMethod]
    [DataRow(UiActivationResultKind.Unavailable)]
    [DataRow(UiActivationResultKind.Failed)]
    public async Task UnavailableOrFailedActivationRetriesWithoutBecomingPrimary(UiActivationResultKind resultKind)
    {
        var processLock = new FakeProcessInstanceLock(isOwner: false);
        var activation = new DelegateWindowsUiActivationClient((_, _) =>
            Task.FromResult(new UiActivationResult(resultKind, "Activation unavailable.")));
        var controller = new WindowsUiSingleInstanceController(
            processLock, activation, maximumActivationAttempts: 3, retryDelay: TimeSpan.Zero);

        var role = await controller.EnterAsync();

        Assert.AreEqual(WindowsUiInstanceRole.SecondaryActivationUnavailable, role);
        Assert.AreEqual(3, activation.CallCount);
        Assert.IsFalse(processLock.IsOwner);
    }

    [TestMethod]
    public async Task PreviousUiExitDuringActivationAllowsCurrentLaunchToBecomePrimary()
    {
        var state = new FakeProcessInstanceLockState();
        using var previous = new FakeProcessInstanceLock(state);
        var contender = new FakeProcessInstanceLock(state);
        var activation = new DelegateWindowsUiActivationClient((_, _) =>
        {
            previous.Dispose();
            return Task.FromResult(new UiActivationResult(
                UiActivationResultKind.Failed,
                "The previous UI activation endpoint is shutting down."));
        });
        var controller = new WindowsUiSingleInstanceController(
            contender,
            activation,
            maximumActivationAttempts: 3,
            retryDelay: TimeSpan.Zero);

        var role = await controller.EnterAsync();

        Assert.AreEqual(WindowsUiInstanceRole.Primary, role);
        Assert.IsTrue(contender.IsOwner);
        Assert.AreEqual(1, activation.CallCount);
        Assert.AreEqual(1, contender.EnsureOwnershipCount);
    }

}
