using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Agent.Native;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Ui;

[TestClass]
public sealed class WindowsNativeShellDispatcherTests
{
    [TestMethod]
    public void MultipleQueuedActionsUseOneNativeWakeAndDrainInOrder()
    {
        var poster = new FakeWindowsNativeMessagePoster();
        using var dispatcher = new WindowsNativeShellDispatcher(
            poster,
            WindowsNativeApplicationLoop.DispatchMessage,
            ownerManagedThreadId: -1);
        var observed = new List<int>();

        Assert.IsTrue(dispatcher.TryPost(() => observed.Add(1)));
        Assert.IsTrue(dispatcher.TryPost(() => observed.Add(2)));

        Assert.AreEqual(1, poster.PostCount);
        Assert.AreEqual(WindowsNativeApplicationLoop.DispatchMessage, poster.LastMessage);
        dispatcher.DrainPendingActions();
        CollectionAssert.AreEqual(new[] { 1, 2 }, observed);
    }

    [TestMethod]
    public void DispatchedFailureIsContainedAndDoesNotPreventLaterWork()
    {
        var poster = new FakeWindowsNativeMessagePoster();
        using var dispatcher = new WindowsNativeShellDispatcher(
            poster,
            WindowsNativeApplicationLoop.DispatchMessage,
            ownerManagedThreadId: -1);
        var completed = false;
        Assert.IsTrue(dispatcher.TryPost(() => throw new InvalidOperationException("test")));
        Assert.IsTrue(dispatcher.TryPost(() => completed = true));

        dispatcher.DrainPendingActions();

        Assert.IsTrue(completed);
    }

    [TestMethod]
    public void StopRejectsAndDiscardsPendingWork()
    {
        var poster = new FakeWindowsNativeMessagePoster();
        using var dispatcher = new WindowsNativeShellDispatcher(
            poster,
            WindowsNativeApplicationLoop.DispatchMessage,
            ownerManagedThreadId: -1);
        var executed = false;
        Assert.IsTrue(dispatcher.TryPost(() => executed = true));

        dispatcher.StopAccepting();
        dispatcher.DrainPendingActions();

        Assert.IsFalse(executed);
        Assert.IsFalse(dispatcher.TryPost(() => executed = true));
    }

    [TestMethod]
    public async Task StopFaultsPendingInvokeInsteadOfLeavingItIncomplete()
    {
        var poster = new FakeWindowsNativeMessagePoster();
        using var dispatcher = new WindowsNativeShellDispatcher(
            poster,
            WindowsNativeApplicationLoop.DispatchMessage,
            ownerManagedThreadId: -1);
        var pending = dispatcher.InvokeAsync(() => { });

        dispatcher.StopAccepting();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => pending);
    }

    [TestMethod]
    public async Task NativeWakeFailureFaultsInvokeAndDoesNotExecuteWork()
    {
        var poster = new FakeWindowsNativeMessagePoster { ShouldSucceed = false };
        using var dispatcher = new WindowsNativeShellDispatcher(
            poster,
            WindowsNativeApplicationLoop.DispatchMessage,
            ownerManagedThreadId: -1);
        var executed = false;

        var pending = dispatcher.InvokeAsync(() => executed = true);

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => pending);
        Assert.IsFalse(executed);
    }

    [TestMethod]
    public void DisposalIsIdempotentAndRejectsNewWork()
    {
        var poster = new FakeWindowsNativeMessagePoster();
        var dispatcher = new WindowsNativeShellDispatcher(
            poster,
            WindowsNativeApplicationLoop.DispatchMessage,
            ownerManagedThreadId: -1);

        dispatcher.Dispose();
        dispatcher.Dispose();

        Assert.IsFalse(dispatcher.TryPost(() => { }));
    }

    [TestMethod]
    public void QueueIsBoundedAndDoesNotPostAdditionalWakeMessages()
    {
        var poster = new FakeWindowsNativeMessagePoster();
        using var dispatcher = new WindowsNativeShellDispatcher(
            poster,
            WindowsNativeApplicationLoop.DispatchMessage,
            ownerManagedThreadId: -1,
            capacity: 2);

        Assert.IsTrue(dispatcher.TryPost(() => { }));
        Assert.IsTrue(dispatcher.TryPost(() => { }));
        Assert.IsFalse(dispatcher.TryPost(() => { }));
        Assert.AreEqual(1, poster.PostCount);
    }
}
