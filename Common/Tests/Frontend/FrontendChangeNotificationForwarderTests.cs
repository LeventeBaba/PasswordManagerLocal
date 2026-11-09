using System.Runtime.CompilerServices;
using System.Threading.Channels;
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

    [TestMethod]
    public async Task StreamOnlyClientForwardsRealWatermarksChangesAndRuntimeReplacement()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runtime = new StreamOnlyRuntime();
        var userId = Guid.NewGuid();
        await using var forwarder = new FrontendChangeNotificationForwarder(
            runtime, null, () => userId, () => true, timeout.Token);
        var hints = Channel.CreateUnbounded<FrontendChangeStreamDelivery>();
        forwarder.ChangeHintReceived += hint => hints.Writer.TryWrite(hint);
        runtime.SetReady(true);
        var subscription = await runtime.Registrations.Reader.ReadAsync(timeout.Token);
        Assert.AreEqual(userId, subscription.UserId);
        Assert.IsNull(forwarder.CurrentPosition, "IPC has no authoritative watermark until Connected.");
        subscription.Events.Writer.TryWrite(new(FrontendChangeStreamEventKind.Connected,
            null, 0, FrontendChangeScope.Everything, true));
        var firstRuntime = Guid.NewGuid();
        subscription.Events.Writer.TryWrite(new(FrontendChangeStreamEventKind.Change,
            firstRuntime, 10, FrontendChangeScope.Passwords, false));
        var connected = await hints.Reader.ReadAsync(timeout.Token);
        Assert.IsTrue(connected.RequiresAuthoritativeRefresh);
        Assert.AreEqual(firstRuntime, connected.RuntimeInstanceId);
        Assert.AreEqual(10L, connected.Sequence);
        subscription.Events.Writer.TryWrite(new(FrontendChangeStreamEventKind.Change,
            firstRuntime, 11, FrontendChangeScope.Passwords, false));
        var changed = await hints.Reader.ReadAsync(timeout.Token);
        Assert.IsFalse(changed.RequiresAuthoritativeRefresh);
        Assert.AreEqual(FrontendChangeScope.Passwords, changed.Notification!.Scope);
        subscription.Events.Writer.TryWrite(new(FrontendChangeStreamEventKind.Disconnected,
            null, 0, FrontendChangeScope.Everything, true));
        var replacementRuntime = Guid.NewGuid();
        subscription.Events.Writer.TryWrite(new(FrontendChangeStreamEventKind.Connected,
            replacementRuntime, 0, FrontendChangeScope.Everything, false));
        var replacement = await hints.Reader.ReadAsync(timeout.Token);
        Assert.AreEqual(replacementRuntime, replacement.RuntimeInstanceId);
        Assert.IsTrue(replacement.RequiresAuthoritativeRefresh);
        Assert.IsFalse(hints.Reader.TryRead(out _));
    }

    [TestMethod]
    public async Task StreamOnlyProfileSwitchCancelsOldFilterAndDeactivationRevokesDelivery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runtime = new StreamOnlyRuntime();
        Guid? userId = Guid.NewGuid();
        await using var forwarder = new FrontendChangeNotificationForwarder(
            runtime, null, () => userId, () => true, timeout.Token);
        var hints = Channel.CreateUnbounded<FrontendChangeStreamDelivery>();
        forwarder.ChangeHintReceived += hint => hints.Writer.TryWrite(hint);
        runtime.SetReady(true);
        var old = await runtime.Registrations.Reader.ReadAsync(timeout.Token);
        userId = Guid.NewGuid();
        forwarder.ReconcileUserScope();
        var current = await runtime.Registrations.Reader.ReadAsync(timeout.Token);
        await old.Cancelled.Task.WaitAsync(timeout.Token);
        Assert.AreEqual(userId, current.UserId);
        old.Events.Writer.TryWrite(new(FrontendChangeStreamEventKind.Change,
            Guid.NewGuid(), 999, FrontendChangeScope.Passwords, false));
        var runtimeId = Guid.NewGuid();
        current.Events.Writer.TryWrite(new(FrontendChangeStreamEventKind.Connected,
            runtimeId, 20, FrontendChangeScope.Everything, true));
        var connected = await hints.Reader.ReadAsync(timeout.Token);
        Assert.AreEqual(runtimeId, connected.RuntimeInstanceId);
        Assert.IsFalse(hints.Reader.TryRead(out _));
        forwarder.Deactivate();
        await forwarder.DisposeAsync();
        await current.Cancelled.Task.WaitAsync(timeout.Token);
        current.Events.Writer.TryWrite(new(FrontendChangeStreamEventKind.Change,
            runtimeId, 21, FrontendChangeScope.Passwords, false));
        forwarder.ReconcileOnResume();
        Assert.IsFalse(hints.Reader.TryRead(out _));
        Assert.IsFalse(runtime.Registrations.Reader.TryRead(out _));
    }

    [TestMethod]
    public async Task StreamReplacementWaitsForOldServerSubscriptionCleanup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runtime = new StreamOnlyRuntime();
        Guid? userId = Guid.NewGuid();
        await using var forwarder = new FrontendChangeNotificationForwarder(
            runtime, null, () => userId, () => true, timeout.Token);
        runtime.SetReady(true);
        var old = await runtime.Registrations.Reader.ReadAsync(timeout.Token);
        var cleanupRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        old.CleanupRelease = cleanupRelease;
        try
        {
            userId = Guid.NewGuid();
            forwarder.ReconcileUserScope();
            await old.CleanupEntered.Task.WaitAsync(timeout.Token);
            Assert.IsFalse(runtime.Registrations.Reader.TryRead(out _),
                "The old Windows producer could still disable the shared server subscription.");
        }
        finally { cleanupRelease.TrySetResult(); }
        var replacement = await runtime.Registrations.Reader.ReadAsync(timeout.Token);
        Assert.IsTrue(old.Cancelled.Task.IsCompleted);
        Assert.AreEqual(userId, replacement.UserId);
    }

    private sealed record StreamRegistration(Guid? UserId)
    {
        public Channel<FrontendChangeStreamEvent> Events { get; } = Channel.CreateBounded<FrontendChangeStreamEvent>(64);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? CleanupRelease { get; set; }
    }

    private sealed class StreamOnlyRuntime : IBackendRuntimeClient, IFrontendChangeStreamClient
    {
        private readonly FakeFrontendRuntimeClient _inner = new();
        public Channel<StreamRegistration> Registrations { get; } = Channel.CreateUnbounded<StreamRegistration>();
        public BackendRuntimeSnapshot Snapshot => _inner.Snapshot;
        public event EventHandler<BackendRuntimeStateChangedEventArgs>? StateChanged
        { add => _inner.StateChanged += value; remove => _inner.StateChanged -= value; }
        public void SetReady(bool ready) => _inner.SetReady(ready);
        public Task ConnectAsync(CancellationToken token = default) => _inner.ConnectAsync(token);
        public Task WaitUntilReadyAsync(CancellationToken token = default) => _inner.WaitUntilReadyAsync(token);
        public Task ResetDatabaseAndRestartAsync(CancellationToken token = default) => _inner.ResetDatabaseAndRestartAsync(token);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
        public async IAsyncEnumerable<FrontendChangeStreamEvent> ReadChangeStreamAsync(Guid? userId = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var registration = new StreamRegistration(userId);
            Registrations.Writer.TryWrite(registration);
            try
            {
                await foreach (var item in registration.Events.Reader.ReadAllAsync(cancellationToken))
                    yield return item;
            }
            finally
            {
                registration.CleanupEntered.TrySetResult();
                if (registration.CleanupRelease is { } release)
                    await release.Task;
                registration.Cancelled.TrySetResult();
            }
        }
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
