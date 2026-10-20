using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Notifications;
using PasswordManagerLocal.Windows.Agent.Notifications;
using PasswordManagerLocal.Windows.Ipc.Authorization;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Transport;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Agent;

[TestClass]
public sealed class WindowsAgentChangeNotificationAdapterTests
{
    [TestMethod]
    public async Task BackendSubscriptionRequiresAnAdmittedNotificationTarget()
    {
        var coordinator = new SingleUiConnectionCoordinator();
        var connection = CreateUiConnection(Guid.NewGuid());
        Assert.IsTrue(coordinator.TryRegister(connection, out _));
        var registry = new WindowsIpcNotificationSessionRegistry();
        var source = new RecordingBackendChangeSource();
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        owner.SetChangeSource(source);
        await using var adapter = new WindowsAgentChangeNotificationAdapter(owner, coordinator, registry);
        await adapter.WaitForScheduledTransitionsAsync();

        Assert.IsFalse(await adapter.SetUserFilterAsync(connection.ConnectionId, Guid.NewGuid(), enabled: true));
        Assert.AreEqual(0, source.ActiveSubscriptionCount);

        var queue = new IpcChangeNotificationQueue();
        Assert.IsTrue(registry.TryRegister(connection.ConnectionId, queue));
        Assert.IsFalse(registry.IsEnabled(connection.ConnectionId));
        Assert.IsTrue(await adapter.SetUserFilterAsync(connection.ConnectionId, null, enabled: true));
        Assert.IsTrue(registry.IsEnabled(connection.ConnectionId));
        Assert.AreEqual(1, source.ActiveSubscriptionCount);
        var start = await queue.ReadAsync();
        Assert.IsNotNull(start);
        Assert.IsTrue(start.RequiresAuthoritativeRefresh);
        Assert.IsTrue(registry.TryEnqueue(
            connection.ConnectionId,
            new IpcChangeNotificationDto(
                Guid.NewGuid(),
                1,
                FrontendChangeScope.Passwords,
                DateTimeOffset.UtcNow,
                RequiresAuthoritativeRefresh: false)));
        Assert.AreEqual(1, queue.Count);

        Assert.IsTrue(await adapter.SetUserFilterAsync(
            connection.ConnectionId,
            userId: null,
            enabled: false));
        Assert.AreEqual(0, source.ActiveSubscriptionCount);
        Assert.AreEqual(0, queue.Count);
        Assert.IsFalse(registry.IsEnabled(connection.ConnectionId));

        coordinator.Unregister(connection.ConnectionId);
        await adapter.WaitForScheduledTransitionsAsync();
        Assert.AreEqual(0, source.ActiveSubscriptionCount);
        registry.Unregister(connection.ConnectionId);
        await queue.DisposeAsync();
    }

    [TestMethod]
    public async Task OnlyRegisteredUiReceivesFilteredMetadataAndDisconnectRemovesSubscription()
    {
        var coordinator = new SingleUiConnectionCoordinator();
        var connection = CreateUiConnection(Guid.NewGuid());
        Assert.IsTrue(coordinator.TryRegister(connection, out _));
        var registry = new WindowsIpcNotificationSessionRegistry();
        var queue = new IpcChangeNotificationQueue();
        Assert.IsTrue(registry.TryRegister(connection.ConnectionId, queue));
        var source = new RecordingBackendChangeSource();
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        owner.SetChangeSource(source);
        await using var adapter = new WindowsAgentChangeNotificationAdapter(owner, coordinator, registry);
        var userId = Guid.NewGuid();

        Assert.IsFalse(await adapter.SetUserFilterAsync(Guid.NewGuid(), userId, enabled: true));
        Assert.IsTrue(await adapter.SetUserFilterAsync(connection.ConnectionId, userId, enabled: true));
        var start = await queue.ReadAsync();
        Assert.IsNotNull(start);
        Assert.IsTrue(start.RequiresAuthoritativeRefresh);
        Assert.AreEqual(FrontendChangeScope.Everything, start.Scope);
        Assert.AreEqual(userId, source.SubscribedUsers.Single());

        source.Publish(FrontendChangeScope.Passwords, Guid.NewGuid());
        source.Publish(FrontendChangeScope.Devices, userId);
        var change = await queue.ReadAsync();

        Assert.IsNotNull(change);
        Assert.AreEqual(2, change.Sequence);
        // Event-hub sequence numbers are global, so user filtering can leave gaps.
        // Phase 14 conservatively turns any observed gap into a full-refresh marker.
        Assert.IsTrue(change.RequiresAuthoritativeRefresh);
        Assert.AreEqual(FrontendChangeScope.Everything, change.Scope);

        source.Publish(FrontendChangeScope.AuthenticationSessions);
        var globalChange = await queue.ReadAsync();
        Assert.IsNotNull(globalChange);
        Assert.AreEqual(3, globalChange.Sequence);
        Assert.AreEqual(FrontendChangeScope.AuthenticationSessions, globalChange.Scope);
        Assert.IsFalse(globalChange.RequiresAuthoritativeRefresh);
        Assert.IsFalse(typeof(IpcChangeNotificationDto).GetProperties()
            .Any(property => property.Name.Contains("User", StringComparison.OrdinalIgnoreCase)));

        coordinator.Unregister(connection.ConnectionId);
        await adapter.WaitForScheduledTransitionsAsync();
        Assert.AreEqual(0, source.ActiveSubscriptionCount);
        registry.Unregister(connection.ConnectionId);
        Assert.IsFalse(registry.TryEnqueue(connection.ConnectionId, change));
        await queue.DisposeAsync();
    }

    [TestMethod]
    public async Task BackendRuntimeReplacementStartsNewWatermarkAndRequiresRefresh()
    {
        var coordinator = new SingleUiConnectionCoordinator();
        var connection = CreateUiConnection(Guid.NewGuid());
        Assert.IsTrue(coordinator.TryRegister(connection, out _));
        var registry = new WindowsIpcNotificationSessionRegistry();
        var queue = new IpcChangeNotificationQueue();
        registry.TryRegister(connection.ConnectionId, queue);
        var firstSource = new RecordingBackendChangeSource();
        var secondSource = new RecordingBackendChangeSource();
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        owner.SetChangeSource(firstSource);
        await using var adapter = new WindowsAgentChangeNotificationAdapter(owner, coordinator, registry);
        var userId = Guid.NewGuid();
        await adapter.SetUserFilterAsync(connection.ConnectionId, userId, enabled: true);
        await queue.ReadAsync();

        owner.SetChangeSource(secondSource);
        await adapter.WaitForScheduledTransitionsAsync();
        var runtimeMarker = await queue.ReadAsync();

        Assert.AreEqual(0, firstSource.ActiveSubscriptionCount);
        Assert.AreEqual(1, secondSource.ActiveSubscriptionCount);
        Assert.IsNotNull(runtimeMarker);
        Assert.AreEqual(secondSource.CurrentPosition.RuntimeInstanceId, runtimeMarker.RuntimeInstanceId);
        Assert.AreEqual(0, runtimeMarker.Sequence);
        Assert.IsTrue(runtimeMarker.RequiresAuthoritativeRefresh);
        Assert.AreEqual(userId, secondSource.SubscribedUsers.Single());

        secondSource.RotateRuntime();
        var rotated = await queue.ReadAsync();
        Assert.IsNotNull(rotated);
        Assert.AreEqual(secondSource.CurrentPosition.RuntimeInstanceId, rotated.RuntimeInstanceId);
        Assert.AreEqual(0, rotated.Sequence);
        Assert.IsTrue(rotated.RequiresAuthoritativeRefresh);
        coordinator.Unregister(connection.ConnectionId);
        await adapter.WaitForScheduledTransitionsAsync();
        Assert.AreEqual(0, secondSource.ActiveSubscriptionCount);
        registry.Unregister(connection.ConnectionId);
        await queue.DisposeAsync();
    }

    [TestMethod]
    public void NotificationUserSelectionRequiresCurrentRegisteredUi()
    {
        var coordinator = new SingleUiConnectionCoordinator();
        var authorizer = new WindowsIpcOperationAuthorizer(coordinator);
        var connection = CreateUiConnection(Guid.NewGuid());
        var serializer = new WindowsIpcSerializer();
        var request = new IpcRequestEnvelope(
            IpcOperationId.SetChangeNotificationUser,
            serializer.Serialize(
                new SetChangeNotificationUserRequestDto(Guid.NewGuid(), Enabled: true),
                WindowsIpcJsonContext.Default.SetChangeNotificationUserRequestDto));
        var context = new IpcRequestContext(
            connection,
            1,
            request,
            serializer);

        Assert.AreEqual(IpcErrorCode.UiNotRegistered, authorizer.Authorize(context).ErrorCode);
        Assert.IsTrue(coordinator.TryRegister(connection, out _));
        Assert.IsTrue(authorizer.Authorize(context).IsAuthorized);

        var noCapabilityContext = new IpcRequestContext(
            connection with { PeerCapabilities = IpcCapabilities.Control },
            1,
            request,
            serializer);
        Assert.AreEqual(
            IpcErrorCode.UnauthorizedOperation,
            authorizer.Authorize(noCapabilityContext).ErrorCode);
    }

    private static IpcConnectionContext CreateUiConnection(Guid sessionId) => new(
        Guid.NewGuid(),
        IpcPeerRole.Ui,
        Environment.ProcessId,
        0,
        sessionId,
        IpcCapabilities.Control | IpcCapabilities.ChangeNotifications);
}
