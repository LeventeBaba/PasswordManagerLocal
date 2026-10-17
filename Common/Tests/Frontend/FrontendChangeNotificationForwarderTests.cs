using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Notifications;
using PasswordManagerLocal.Common.Contracts.Notifications;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Frontend;

[TestClass]
public sealed class FrontendChangeNotificationForwarderTests
{
    [TestMethod]
    public async Task ClosedAndReopenedSessionStartsAtLatestWatermarkWithoutRetainingBacklog()
    {
        var source = new FakeBackendChangeSource(
            Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var runtime = new FakeFrontendRuntimeClient();
        var notificationClient = new FakeFrontendChangeNotificationClient(source);
        using var lifetime = new CancellationTokenSource();
        var oldSessionActive = true;
        var oldForwarder = new FrontendChangeNotificationForwarder(
            runtime,
            notificationClient,
            () => null,
            () => oldSessionActive,
            lifetime.Token);
        var oldStart = CaptureStart(oldForwarder);
        runtime.SetReady(true);
        var initial = await oldStart.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(0L, initial.Sequence);
        Assert.AreEqual(1, source.ActiveSubscriptionCount);

        oldSessionActive = false;
        oldForwarder.Deactivate();
        await oldForwarder.DisposeAsync();
        Assert.AreEqual(0, source.ActiveSubscriptionCount);

        source.Publish(FrontendChangeScope.Passwords);
        Assert.AreEqual(1L, source.CurrentPosition.Sequence);

        using var replacementLifetime = new CancellationTokenSource();
        var replacement = new FrontendChangeNotificationForwarder(
            runtime,
            notificationClient,
            () => null,
            () => true,
            replacementLifetime.Token);
        var replacementStart = CaptureStart(replacement);
        replacement.ReconcileOnResume();
        var reopened = await replacementStart.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(source.CurrentPosition.Sequence, reopened.Sequence);
        Assert.AreEqual(source.CurrentPosition.RuntimeInstanceId, reopened.RuntimeInstanceId);
        Assert.IsTrue(reopened.RequiresAuthoritativeRefresh);
        Assert.AreEqual(1, source.ActiveSubscriptionCount);
        await replacement.DisposeAsync();
    }

    [TestMethod]
    public async Task RotationKeepsOnlyReplacementSessionAndIgnoresQueuedOldActivityDelivery()
    {
        var source = new FakeBackendChangeSource();
        var runtime = new FakeFrontendRuntimeClient();
        var notificationClient = new FakeFrontendChangeNotificationClient(source)
        {
            GateNextDelivery = true
        };
        using var oldLifetime = new CancellationTokenSource();
        var oldForwarder = new FrontendChangeNotificationForwarder(
            runtime,
            notificationClient,
            () => null,
            () => true,
            oldLifetime.Token);
        var received = new ConcurrentQueue<FrontendChangeStreamDelivery>();
        oldForwarder.ChangeHintReceived += received.Enqueue;
        runtime.SetReady(true);
        Assert.AreEqual(1, source.ActiveSubscriptionCount);

        source.Publish(FrontendChangeScope.Passwords);
        var oldSubscription = notificationClient.LastSubscription!;
        await oldSubscription.DeliveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        oldForwarder.Deactivate();
        using var replacementLifetime = new CancellationTokenSource();
        var replacement = new FrontendChangeNotificationForwarder(
            runtime,
            notificationClient,
            () => null,
            () => true,
            replacementLifetime.Token);
        var replacementStart = CaptureStart(replacement);
        replacement.ReconcileOnResume();
        var marker = await replacementStart.Task.WaitAsync(TimeSpan.FromSeconds(2));
        oldSubscription.ReleaseDelivery.TrySetResult();
        await oldForwarder.DisposeAsync();

        Assert.IsTrue(marker.RequiresAuthoritativeRefresh);
        Assert.AreEqual(1, source.ActiveSubscriptionCount);
        Assert.IsTrue(received.All(delivery => delivery.Notification is null));
        await replacement.DisposeAsync();
    }

    [TestMethod]
    public async Task RuntimeSourceReplacementResubscribesAtNewRuntimeWatermark()
    {
        var firstSource = new FakeBackendChangeSource(
            Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var secondSource = new FakeBackendChangeSource(
            Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var runtime = new FakeFrontendRuntimeClient();
        var notificationClient = new FakeFrontendChangeNotificationClient(firstSource);
        using var lifetime = new CancellationTokenSource();
        var forwarder = new FrontendChangeNotificationForwarder(
            runtime,
            notificationClient,
            () => null,
            () => true,
            lifetime.Token);
        runtime.SetReady(true);
        Assert.AreEqual(1, firstSource.ActiveSubscriptionCount);

        var replacementStart = CaptureStart(forwarder);
        notificationClient.ReplaceSource(secondSource);
        var marker = await replacementStart.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(0, firstSource.ActiveSubscriptionCount);
        Assert.AreEqual(1, secondSource.ActiveSubscriptionCount);
        Assert.AreEqual(secondSource.CurrentPosition.RuntimeInstanceId, marker.RuntimeInstanceId);
        Assert.IsTrue(marker.RequiresAuthoritativeRefresh);
        await forwarder.DisposeAsync();
    }

    [TestMethod]
    public async Task StaleActivityCallbackAfterSynchronousDeactivationIsIgnored()
    {
        var source = new FakeBackendChangeSource();
        var runtime = new FakeFrontendRuntimeClient();
        var notificationClient = new FakeFrontendChangeNotificationClient(source)
        {
            GateNextDelivery = true
        };
        using var lifetime = new CancellationTokenSource();
        var forwarder = new FrontendChangeNotificationForwarder(
            runtime,
            notificationClient,
            () => null,
            () => true,
            lifetime.Token);
        var delivered = new ConcurrentQueue<FrontendChangeStreamDelivery>();
        forwarder.ChangeHintReceived += delivered.Enqueue;
        runtime.SetReady(true);
        Assert.AreEqual(1, delivered.Count);

        source.Publish(FrontendChangeScope.Passwords);
        var blocked = notificationClient.LastSubscription!;
        await blocked.DeliveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        forwarder.Deactivate();
        blocked.ReleaseDelivery.TrySetResult();
        await forwarder.DisposeAsync();

        Assert.AreEqual(1, delivered.Count);
        Assert.IsTrue(delivered.Single().RequiresAuthoritativeRefresh);
        Assert.AreEqual(0, source.ActiveSubscriptionCount);
    }

    [TestMethod]
    public async Task RepeatedResumeAndDoubleDetachDoNotCreateConcurrentSubscriptions()
    {
        var source = new FakeBackendChangeSource();
        var runtime = new FakeFrontendRuntimeClient();
        var notificationClient = new FakeFrontendChangeNotificationClient(source);
        using var lifetime = new CancellationTokenSource();
        var forwarder = new FrontendChangeNotificationForwarder(
            runtime,
            notificationClient,
            () => null,
            () => true,
            lifetime.Token);
        runtime.SetReady(true);

        forwarder.ReconcileOnResume();
        Assert.AreEqual(1, source.ActiveSubscriptionCount);
        forwarder.ReconcileOnResume();
        Assert.AreEqual(1, source.ActiveSubscriptionCount);
        Assert.AreEqual(3, source.TotalSubscriptionCount);

        forwarder.Deactivate();
        forwarder.Deactivate();
        await forwarder.DisposeAsync();
        await forwarder.DisposeAsync();
        Assert.AreEqual(0, source.ActiveSubscriptionCount);
    }

    private static TaskCompletionSource<FrontendChangeStreamDelivery> CaptureStart(
        FrontendChangeNotificationForwarder forwarder)
    {
        var completion = new TaskCompletionSource<FrontendChangeStreamDelivery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        forwarder.ChangeHintReceived += delivery =>
        {
            if (delivery.RequiresAuthoritativeRefresh)
                completion.TrySetResult(delivery);
        };
        return completion;
    }

    private sealed class FakeFrontendRuntimeClient : IBackendRuntimeClient
    {
        private BackendRuntimeSnapshot _snapshot = CreateSnapshot(BackendRuntimeState.NotStarted);

        public BackendRuntimeSnapshot Snapshot => _snapshot;
        public event EventHandler<BackendRuntimeStateChangedEventArgs>? StateChanged;

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetReady(true);
            return Task.CompletedTask;
        }

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetReady(true);
            return Task.CompletedTask;
        }

        public Task ResetDatabaseAndRestartAsync(CancellationToken cancellationToken = default) =>
            ConnectAsync(cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void SetReady(bool ready)
        {
            var previous = _snapshot;
            _snapshot = CreateSnapshot(ready ? BackendRuntimeState.Ready : BackendRuntimeState.Stopped);
            StateChanged?.Invoke(this, new BackendRuntimeStateChangedEventArgs(previous, _snapshot));
        }

        private static BackendRuntimeSnapshot CreateSnapshot(BackendRuntimeState state) => new(
            state,
            BackendRuntimeFailureKind.None,
            null,
            DateTimeOffset.UnixEpoch);
    }

    private sealed class FakeFrontendChangeNotificationClient : IFrontendChangeNotificationClient
    {
        private FakeBackendChangeSource _source;

        public FakeFrontendChangeNotificationClient(FakeBackendChangeSource source) =>
            _source = source;

        public event EventHandler? ChangeSourceChanged;
        public bool GateNextDelivery { get; init; }
        public GatedFrontendSubscription? LastSubscription { get; private set; }

        public IFrontendChangeNotificationSubscription SubscribeChangeNotifications(
            Guid? userId = null,
            int capacity = 64)
        {
            var inner = _source.Subscribe(userId, capacity);
            var subscription = new GatedFrontendSubscription(inner, GateNextDelivery);
            LastSubscription = subscription;
            return subscription;
        }

        public void ReplaceSource(FakeBackendChangeSource source)
        {
            _source = source;
            ChangeSourceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class GatedFrontendSubscription : IFrontendChangeNotificationSubscription
    {
        private readonly IBackendChangeSubscription _inner;
        private readonly bool _gateDelivery;
        private readonly object _disposeGate = new();
        private Task? _disposeTask;
        private int _unsubscribed;

        public GatedFrontendSubscription(IBackendChangeSubscription inner, bool gateDelivery)
        {
            _inner = inner;
            _gateDelivery = gateDelivery;
            Start = new FrontendChangeStreamStart(
                inner.Start.RuntimeInstanceId,
                inner.Start.Sequence,
                inner.Start.RequiresAuthoritativeRefresh);
        }

        public FrontendChangeStreamStart Start { get; }
        public TaskCompletionSource DeliveryEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDelivery { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<FrontendChangeStreamDelivery> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var readToken = _gateDelivery ? CancellationToken.None : cancellationToken;
            await foreach (var delivery in _inner.ReadAllAsync(readToken).ConfigureAwait(false))
            {
                if (_gateDelivery)
                {
                    DeliveryEntered.TrySetResult();
                    await ReleaseDelivery.Task.ConfigureAwait(false);
                }

                yield return new FrontendChangeStreamDelivery(
                    delivery.RuntimeInstanceId,
                    delivery.Sequence,
                    delivery.Notification,
                    delivery.RequiresAuthoritativeRefresh);
            }
        }

        public void Unsubscribe()
        {
            if (Interlocked.Exchange(ref _unsubscribed, 1) != 0)
                return;
            lock (_disposeGate)
                _disposeTask = _inner.DisposeAsync().AsTask();
        }

        public async ValueTask DisposeAsync()
        {
            Unsubscribe();
            Task? dispose;
            lock (_disposeGate)
                dispose = _disposeTask;
            if (dispose is not null)
                await dispose.ConfigureAwait(false);
        }
    }
}
