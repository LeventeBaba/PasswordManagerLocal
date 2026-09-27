using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Agent.Lifecycle;

namespace PasswordManagerLocal.Windows.Tests.IPC.Agent;

[TestClass]
public sealed class WindowsAgentLifecycleTransitionCoordinatorTests
{
    [TestMethod]
    public async Task ConcurrentTransitionsAreSerializedAndStateIsTruthful()
    {
        using var coordinator = new WindowsAgentLifecycleTransitionCoordinator();
        await using var first = await coordinator.EnterAsync(
            WindowsAgentLifecycleTransitionState.ChangingBackgroundSync);
        var second = coordinator.EnterAsync(
            WindowsAgentLifecycleTransitionState.ResettingDatabase);

        await Task.Delay(25);

        Assert.AreEqual(
            WindowsAgentLifecycleTransitionState.ChangingBackgroundSync,
            coordinator.State);
        Assert.IsFalse(second.IsCompleted);

        await first.DisposeAsync();
        await using var acquiredSecond = await second;

        Assert.AreEqual(
            WindowsAgentLifecycleTransitionState.ResettingDatabase,
            coordinator.State);
        await acquiredSecond.DisposeAsync();
        Assert.AreEqual(WindowsAgentLifecycleTransitionState.Idle, coordinator.State);
    }

    [TestMethod]
    public async Task CancelledWaitDoesNotStealTransitionOwnership()
    {
        using var coordinator = new WindowsAgentLifecycleTransitionCoordinator();
        await using var owner = await coordinator.EnterAsync(
            WindowsAgentLifecycleTransitionState.ShuttingDown);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.EnterAsync(
                WindowsAgentLifecycleTransitionState.ResettingDatabase,
                cancellation.Token));

        Assert.AreEqual(
            WindowsAgentLifecycleTransitionState.ShuttingDown,
            coordinator.State);
    }

    [TestMethod]
    public async Task LeaseDisposalIsIdempotent()
    {
        using var coordinator = new WindowsAgentLifecycleTransitionCoordinator();
        var lease = await coordinator.EnterAsync(
            WindowsAgentLifecycleTransitionState.ChangingBackgroundSync);

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.AreEqual(WindowsAgentLifecycleTransitionState.Idle, coordinator.State);
    }
}
