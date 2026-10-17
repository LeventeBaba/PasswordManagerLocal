using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Notifications;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Contracts.Notifications;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class BackendChangeEventHubTests
{
    [TestMethod]
    [TestCategory("Backend")]
    public void NewHubs_HaveIndependentRuntimeIdentitiesAndSequenceSpaces()
    {
        using var first = new BackendChangeEventHub(TimeProvider.System);
        using var second = new BackendChangeEventHub(TimeProvider.System);

        var firstEvent = first.Publish(FrontendChangeScope.Passwords);
        var secondEvent = second.Publish(FrontendChangeScope.Devices);

        Assert.AreNotEqual(firstEvent!.RuntimeInstanceId, secondEvent!.RuntimeInstanceId);
        Assert.AreEqual(1L, firstEvent.Sequence);
        Assert.AreEqual(1L, secondEvent.Sequence);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public async Task Subscribe_CapturesWatermarkAndBuffersEveryLaterMatchingEvent()
    {
        using var hub = new BackendChangeEventHub(TimeProvider.System);
        hub.Publish(FrontendChangeScope.Passwords);
        await using var subscription = hub.Subscribe(capacity: 8);

        Assert.AreEqual(1L, subscription.Start.Sequence);
        Assert.IsTrue(subscription.Start.RequiresAuthoritativeRefresh);
        Assert.AreEqual(hub.CurrentPosition.RuntimeInstanceId, subscription.Start.RuntimeInstanceId);

        var published = hub.Publish(FrontendChangeScope.Devices)!;
        var deliveries = await ReadExactlyAsync(subscription, 1);

        Assert.AreEqual(published.Sequence, deliveries[0].Sequence);
        Assert.AreEqual(published.RuntimeInstanceId, deliveries[0].RuntimeInstanceId);
        Assert.AreEqual(published, deliveries[0].Notification);
        Assert.IsFalse(deliveries[0].RequiresAuthoritativeRefresh);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public async Task ConcurrentPublications_AreSequencedAndDeliveredInOrder()
    {
        using var hub = new BackendChangeEventHub(TimeProvider.System);
        await using var subscription = hub.Subscribe(capacity: 128);

        Parallel.For(0, 100, _ => hub.Publish(FrontendChangeScope.BackgroundSync));

        var deliveries = await ReadExactlyAsync(subscription, 100);
        CollectionAssert.AreEqual(
            Enumerable.Range(1, 100).Select(value => (long)value).ToArray(),
            deliveries.Select(delivery => delivery.Sequence).ToArray());
        Assert.IsTrue(deliveries.All(delivery => delivery.Notification?.Scope == FrontendChangeScope.BackgroundSync));
    }

    [TestMethod]
    [TestCategory("Backend")]
    public async Task UserSubscriptions_ReceiveOnlyTheirOwnAndGlobalEvents()
    {
        using var hub = new BackendChangeEventHub(TimeProvider.System);
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        await using var userSubscription = hub.Subscribe(userId, capacity: 8);
        await using var globalSubscription = hub.Subscribe(capacity: 8);

        hub.Publish(FrontendChangeScope.Passwords, otherUserId);
        hub.Publish(FrontendChangeScope.UserProfile, userId);
        hub.Publish(FrontendChangeScope.DevicePresence);

        var userDeliveries = await ReadExactlyAsync(userSubscription, 2);
        var globalDeliveries = await ReadExactlyAsync(globalSubscription, 1);

        CollectionAssert.AreEqual(
            new Guid?[] { userId, null },
            userDeliveries.Select(delivery => delivery.Notification!.UserId).ToArray());
        Assert.AreEqual(userId, userDeliveries[0].Notification!.UserId);
        Assert.IsNull(globalDeliveries[0].Notification!.UserId);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public async Task QueueOverflow_ProducesAnExplicitRefreshMarker()
    {
        using var hub = new BackendChangeEventHub(TimeProvider.System);
        var userId = Guid.NewGuid();
        await using var subscription = hub.Subscribe(userId, capacity: 1);

        hub.Publish(FrontendChangeScope.Passwords, userId);
        hub.Publish(FrontendChangeScope.Devices, userId);

        var delivery = (await ReadExactlyAsync(subscription, 1)).Single();
        Assert.IsNull(delivery.Notification);
        Assert.IsTrue(delivery.RequiresAuthoritativeRefresh);
        Assert.AreEqual(hub.CurrentPosition.RuntimeInstanceId, delivery.RuntimeInstanceId);
        Assert.AreEqual(2L, delivery.Sequence);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public async Task SequenceOverflow_RotatesRuntimeIdentityAndSignalsRefresh()
    {
        var previousRuntimeId = Guid.NewGuid();
        using var hub = new BackendChangeEventHub(TimeProvider.System, previousRuntimeId, long.MaxValue);
        await using var subscription = hub.Subscribe(Guid.NewGuid(), capacity: 8);

        var published = hub.Publish(FrontendChangeScope.DevicePresence)!;
        var deliveries = await ReadExactlyAsync(subscription, 2);

        Assert.AreNotEqual(previousRuntimeId, published.RuntimeInstanceId);
        Assert.AreEqual(1L, published.Sequence);
        Assert.IsTrue(deliveries[0].RequiresAuthoritativeRefresh);
        Assert.IsNull(deliveries[0].Notification);
        Assert.AreEqual(0L, deliveries[0].Sequence);
        Assert.AreEqual(published.RuntimeInstanceId, deliveries[1].RuntimeInstanceId);
        Assert.AreEqual(1L, deliveries[1].Sequence);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public async Task DisposedSubscriptionStopsReceivingAndDoesNotAffectOtherObservers()
    {
        using var hub = new BackendChangeEventHub(TimeProvider.System);
        var userId = Guid.NewGuid();
        var removed = hub.Subscribe(userId, capacity: 8);
        await using var active = hub.Subscribe(userId, capacity: 8);
        await removed.DisposeAsync();

        hub.Publish(FrontendChangeScope.Passwords, userId);

        Assert.AreEqual(1, (await ReadExactlyAsync(active, 1)).Count);
        var completed = await ReadToEndAsync(removed);
        Assert.AreEqual(0, completed.Count);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void NotificationContract_ContainsOnlyInvalidationMetadata()
    {
        var propertyNames = typeof(FrontendChangeNotification)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "OccurredAtUtc", "RuntimeInstanceId", "Scope", "Sequence", "UserId" },
            propertyNames);
    }

    private static async Task<IReadOnlyList<BackendChangeDelivery>> ReadExactlyAsync(
        IBackendChangeSubscription subscription,
        int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var deliveries = new List<BackendChangeDelivery>(count);
        await foreach (var delivery in subscription.ReadAllAsync(timeout.Token))
        {
            deliveries.Add(delivery);
            if (deliveries.Count == count)
                return deliveries;
        }

        return deliveries;
    }

    private static async Task<IReadOnlyList<BackendChangeDelivery>> ReadToEndAsync(
        IBackendChangeSubscription subscription)
    {
        var deliveries = new List<BackendChangeDelivery>();
        await foreach (var delivery in subscription.ReadAllAsync())
            deliveries.Add(delivery);
        return deliveries;
    }
}
