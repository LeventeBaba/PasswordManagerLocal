using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Notifications;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Transport;

namespace PasswordManagerLocal.Windows.Tests.IPC.ClientServer;

[TestClass]
public sealed class IpcChangeNotificationQueueTests
{
    [TestMethod]
    public async Task AdjacentNotificationsMergeScopesAndKeepHighestSequenceAndWatermark()
    {
        await using var queue = new IpcChangeNotificationQueue(capacity: 4);
        var runtime = Guid.NewGuid();
        queue.TryEnqueue(Create(runtime, 2, FrontendChangeScope.Passwords, at: 2));
        queue.TryEnqueue(Create(runtime, 5, FrontendChangeScope.Devices, at: 5));

        var item = await queue.ReadAsync();

        Assert.IsNotNull(item);
        Assert.AreEqual(runtime, item.RuntimeInstanceId);
        Assert.AreEqual(5, item.Sequence);
        Assert.AreEqual(FrontendChangeScope.Passwords | FrontendChangeScope.Devices, item.Scope);
        Assert.AreEqual(5, item.OccurredAtUtc.Second);
        Assert.IsFalse(item.RequiresAuthoritativeRefresh);
        Assert.AreEqual(0, queue.Count);
    }

    [TestMethod]
    public async Task OverflowCollapsesPendingWorkToAFullRefreshAtLatestRuntimeWatermark()
    {
        await using var queue = new IpcChangeNotificationQueue(capacity: 2);
        queue.TryEnqueue(Create(Guid.NewGuid(), 1, FrontendChangeScope.Passwords, at: 1));
        queue.TryEnqueue(Create(Guid.NewGuid(), 2, FrontendChangeScope.Devices, at: 2));
        var currentRuntime = Guid.NewGuid();
        queue.TryEnqueue(Create(currentRuntime, 9, FrontendChangeScope.UserProfile, at: 9));

        Assert.AreEqual(1, queue.Count);
        var marker = await queue.ReadAsync();

        Assert.IsNotNull(marker);
        Assert.AreEqual(0, queue.Count);
        Assert.AreEqual(currentRuntime, marker.RuntimeInstanceId);
        Assert.AreEqual(9, marker.Sequence);
        Assert.AreEqual(FrontendChangeScope.Everything, marker.Scope);
        Assert.IsTrue(marker.RequiresAuthoritativeRefresh);
    }

    [TestMethod]
    public async Task RuntimeChangePreservesQueueOrderUntilOverflowAndThenResyncs()
    {
        await using var queue = new IpcChangeNotificationQueue(capacity: 3);
        var firstRuntime = Guid.NewGuid();
        var secondRuntime = Guid.NewGuid();
        queue.TryEnqueue(Create(firstRuntime, 7, FrontendChangeScope.Passwords, at: 1));
        queue.TryEnqueue(Create(secondRuntime, 0, FrontendChangeScope.Everything, at: 2, refresh: true));

        var first = await queue.ReadAsync();
        var second = await queue.ReadAsync();

        Assert.AreEqual(firstRuntime, first?.RuntimeInstanceId);
        Assert.AreEqual(7, first?.Sequence);
        Assert.AreEqual(secondRuntime, second?.RuntimeInstanceId);
        Assert.AreEqual(0, second?.Sequence);
        Assert.IsTrue(second!.RequiresAuthoritativeRefresh);
    }

    [TestMethod]
    public async Task CancellationAndDisposalDoNotRetainQueuedCallbacks()
    {
        await using var queue = new IpcChangeNotificationQueue();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await queue.ReadAsync(cancellation.Token);
        });

        queue.TryEnqueue(Create(Guid.NewGuid(), 1, FrontendChangeScope.Passwords, at: 1));
        await queue.DisposeAsync();

        Assert.IsNull(await queue.ReadAsync());
        Assert.IsFalse(queue.TryEnqueue(Create(Guid.NewGuid(), 2, FrontendChangeScope.Devices, at: 2)));
    }

    private static IpcChangeNotificationDto Create(
        Guid runtime,
        long sequence,
        FrontendChangeScope scope,
        int at,
        bool refresh = false) => new(
            runtime,
            sequence,
            scope,
            new DateTimeOffset(2026, 10, 17, 12, 0, at, TimeSpan.Zero),
            refresh);
}
