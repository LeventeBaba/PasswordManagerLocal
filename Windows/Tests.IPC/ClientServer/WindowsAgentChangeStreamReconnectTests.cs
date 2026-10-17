using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Notifications;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Frontend.AgentConnection;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.ClientServer;

[TestClass]
public sealed class WindowsAgentChangeStreamReconnectTests
{
    [TestMethod]
    public async Task AgentDisconnectAndRestartEmitResyncMarkersAndReapplyUserFilter()
    {
        var firstConnection = new FakeWindowsAgentRegisteredConnection
        {
            AgentProcessId = 6100
        };
        var replacementConnection = new FakeWindowsAgentRegisteredConnection
        {
            AgentProcessId = 6101
        };
        var connector = new FakeWindowsAgentControlConnector();
        connector.Enqueue(firstConnection);
        connector.Enqueue(replacementConnection);
        await using var client = new WindowsAgentControlConnection(
            "change-stream-test",
            new WindowsUiIpcIdentity(Environment.ProcessId, 0, Guid.NewGuid()),
            new FakeWindowsAgentLauncher(),
            connector,
            maximumConnectionAttempts: 1,
            retryDelay: TimeSpan.Zero);
        var userId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        await using var stream = client.ReadChangeStreamAsync(userId, cancellation.Token).GetAsyncEnumerator();

        Assert.IsTrue(await stream.MoveNextAsync());
        Assert.AreEqual(FrontendChangeStreamEventKind.Connected, stream.Current.Kind);
        Assert.IsTrue(stream.Current.RequiresAuthoritativeRefresh);
        Assert.AreEqual(userId, firstConnection.SelectedNotificationUserId);

        var notification = new IpcChangeNotificationDto(
            Guid.NewGuid(),
            4,
            FrontendChangeScope.Devices,
            DateTimeOffset.UtcNow,
            RequiresAuthoritativeRefresh: false);
        firstConnection.PublishNotification(notification);
        Assert.IsTrue(await stream.MoveNextAsync());
        Assert.AreEqual(FrontendChangeStreamEventKind.Change, stream.Current.Kind);
        Assert.AreEqual(4, stream.Current.Sequence);

        firstConnection.Disconnect();
        Assert.IsTrue(await stream.MoveNextAsync());
        Assert.AreEqual(FrontendChangeStreamEventKind.Disconnected, stream.Current.Kind);
        Assert.IsTrue(stream.Current.RequiresAuthoritativeRefresh);

        Assert.IsTrue(await stream.MoveNextAsync());
        Assert.AreEqual(FrontendChangeStreamEventKind.Connected, stream.Current.Kind);
        Assert.AreEqual(userId, replacementConnection.SelectedNotificationUserId);
        Assert.AreEqual(1, replacementConnection.NotificationUserSelectionCount);

        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await stream.MoveNextAsync();
        });
        Assert.IsFalse(replacementConnection.NotificationsEnabled);
    }

    [TestMethod]
    public async Task DisposingControlClientSuppressesFurtherStreamCallbacks()
    {
        var connection = new FakeWindowsAgentRegisteredConnection();
        var connector = new FakeWindowsAgentControlConnector();
        connector.Enqueue(connection);
        await using var client = new WindowsAgentControlConnection(
            "change-stream-disposal-test",
            new WindowsUiIpcIdentity(Environment.ProcessId, 0, Guid.NewGuid()),
            new FakeWindowsAgentLauncher(),
            connector,
            maximumConnectionAttempts: 1,
            retryDelay: TimeSpan.Zero);
        await using var stream = client.ReadChangeStreamAsync().GetAsyncEnumerator();

        Assert.IsTrue(await stream.MoveNextAsync());
        Assert.AreEqual(FrontendChangeStreamEventKind.Connected, stream.Current.Kind);
        await client.DisposeAsync();

        Assert.IsFalse(await stream.MoveNextAsync());
    }
}
