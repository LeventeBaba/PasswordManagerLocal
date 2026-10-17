using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Notifications;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Frontend.Services;

namespace PasswordManagerLocal.Common.Tests.Frontend;

[TestClass]
public sealed class FrontendRefreshCoordinatorTests
{
    private static readonly Guid RuntimeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [TestMethod]
    public async Task IdleFrontendMakesNoRecurringAuthenticationOrEnrollmentStatusCalls()
    {
        var source = new FakeChangeHintSource();
        var delay = new FakeRefreshDelay();
        var endpoints = new FakeFrontendRefreshEndpointClient();
        var target = new FakeFrontendRefreshTarget(endpoints)
        {
            VisiblePages = [FrontendRefreshPage.AuthenticationSessions, FrontendRefreshPage.Enrollment]
        };
        await using var coordinator = CreateCoordinator(source, target, delay);
        coordinator.Start(target.VisiblePages, new FrontendChangeStreamStart(RuntimeId, 0, false));

        await coordinator.WaitForIdleAsync();

        Assert.AreEqual(0, target.RevalidationCount);
        Assert.AreEqual(0, endpoints.Calls.Count);
        Assert.AreEqual(0, delay.ScheduledCount);
    }

    [TestMethod]
    public async Task BurstMergesScopesAndUsesOneCachedDeviceRead()
    {
        var source = new FakeChangeHintSource();
        var delay = new FakeRefreshDelay();
        var endpoints = new FakeFrontendRefreshEndpointClient();
        var target = new FakeFrontendRefreshTarget(endpoints)
        {
            VisiblePages = [FrontendRefreshPage.Passwords, FrontendRefreshPage.Devices]
        };
        await using var coordinator = CreateCoordinator(source, target, delay);
        coordinator.Start(target.VisiblePages, new FrontendChangeStreamStart(RuntimeId, 0, false));

        source.Publish(1, FrontendChangeScope.Passwords);
        source.Publish(2, FrontendChangeScope.Devices);
        source.Publish(3, FrontendChangeScope.DevicePresence);
        await delay.WaitForScheduledCountAsync(3);
        delay.AdvanceAll();

        await endpoints.WaitForCallCountAsync(2);
        await coordinator.WaitForIdleAsync();
        Assert.AreEqual(1, endpoints.Calls.Count(call => call.Page == FrontendRefreshPage.Passwords));
        Assert.IsTrue(target.Dispatcher.DispatchCount > 0);
        var deviceCall = endpoints.Calls.Single(call => call.Page == FrontendRefreshPage.Devices);
        Assert.AreEqual(
            FrontendChangeScope.Devices | FrontendChangeScope.DevicePresence,
            deviceCall.Scopes);
        Assert.AreEqual(1, endpoints.MaximumConcurrentCalls);
    }

    [TestMethod]
    public async Task SequenceGapRequestsFullAuthoritativeReconciliation()
    {
        var source = new FakeChangeHintSource();
        var delay = new FakeRefreshDelay();
        var endpoints = new FakeFrontendRefreshEndpointClient();
        var target = new FakeFrontendRefreshTarget(endpoints)
        {
            VisiblePages = Enum.GetValues<FrontendRefreshPage>()
        };
        await using var coordinator = CreateCoordinator(source, target, delay);
        coordinator.Start(target.VisiblePages, new FrontendChangeStreamStart(RuntimeId, 0, false));

        source.Publish(1, FrontendChangeScope.Passwords);
        await delay.WaitForScheduledCountAsync(1);
        delay.AdvanceAll();
        await endpoints.WaitForCallCountAsync(1);
        await coordinator.WaitForIdleAsync();

        source.Publish(3, FrontendChangeScope.Passwords);
        await delay.WaitForScheduledCountAsync(2);
        delay.AdvanceAll();
        await endpoints.WaitForCallCountAsync(8);
        await coordinator.WaitForIdleAsync();

        Assert.AreEqual(
            Enum.GetValues<FrontendRefreshPage>().Length,
            endpoints.Calls.Skip(1).Select(call => call.Page).Distinct().Count());
        Assert.IsTrue(endpoints.Calls.Any(call => call.Page == FrontendRefreshPage.AuthenticationSessions));
        Assert.AreEqual(1, target.RevalidationCount);
    }

    [TestMethod]
    public async Task StartMarkerReconnectMarkerAndResumeRequestFullReconciliation()
    {
        var source = new FakeChangeHintSource();
        var delay = new FakeRefreshDelay();
        var endpoints = new FakeFrontendRefreshEndpointClient();
        var target = new FakeFrontendRefreshTarget(endpoints)
        {
            VisiblePages = Enum.GetValues<FrontendRefreshPage>()
        };
        await using var coordinator = CreateCoordinator(source, target, delay);
        coordinator.Start(target.VisiblePages, new FrontendChangeStreamStart(RuntimeId, 0, true));

        await delay.WaitForScheduledCountAsync(1);
        delay.AdvanceAll();
        await endpoints.WaitForCallCountAsync(7);
        await coordinator.WaitForIdleAsync();

        source.PublishMarker(0, RuntimeId);
        await delay.WaitForScheduledCountAsync(2);
        delay.AdvanceAll();
        await endpoints.WaitForCallCountAsync(14);
        await coordinator.WaitForIdleAsync();

        coordinator.RequestAuthoritativeRefresh();
        await delay.WaitForScheduledCountAsync(3);
        delay.AdvanceAll();
        await endpoints.WaitForCallCountAsync(21);
        await coordinator.WaitForIdleAsync();

        Assert.AreEqual(21, endpoints.Calls.Count);
        Assert.AreEqual(3, target.RevalidationCount);
    }

    [TestMethod]
    public async Task HiddenPageBecomesDirtyAndRefreshesWhenShown()
    {
        var source = new FakeChangeHintSource();
        var delay = new FakeRefreshDelay();
        var endpoints = new FakeFrontendRefreshEndpointClient();
        var target = new FakeFrontendRefreshTarget(endpoints)
        {
            VisiblePages = [FrontendRefreshPage.Passwords]
        };
        await using var coordinator = CreateCoordinator(source, target, delay);
        coordinator.Start(target.VisiblePages, new FrontendChangeStreamStart(RuntimeId, 0, false));

        source.Publish(1, FrontendChangeScope.Devices);
        await delay.WaitForScheduledCountAsync(1);
        delay.AdvanceAll();
        await coordinator.WaitForIdleAsync();

        Assert.IsTrue(coordinator.IsDirty(FrontendRefreshPage.Devices));
        Assert.AreEqual(0, endpoints.Calls.Count);

        target.VisiblePages = [FrontendRefreshPage.Devices];
        coordinator.OnNavigationChanged(target.VisiblePages);
        await delay.WaitForScheduledCountAsync(2);
        delay.AdvanceAll();
        await endpoints.WaitForCallCountAsync(1);
        await coordinator.WaitForIdleAsync();

        Assert.IsFalse(coordinator.IsDirty(FrontendRefreshPage.Devices));
        Assert.AreEqual(FrontendChangeScope.Devices,
            endpoints.Calls[0].Scopes & (FrontendChangeScope.Devices | FrontendChangeScope.DevicePresence));
    }

    [TestMethod]
    public async Task NavigationCancelsInFlightPageAndKeepsItDirty()
    {
        var source = new FakeChangeHintSource();
        var delay = new FakeRefreshDelay();
        var endpoints = new FakeFrontendRefreshEndpointClient();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        endpoints.BeforeResult = async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            try
            {
                await release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult();
                throw;
            }
        };
        var target = new FakeFrontendRefreshTarget(endpoints)
        {
            VisiblePages = [FrontendRefreshPage.Passwords]
        };
        await using var coordinator = CreateCoordinator(source, target, delay);
        coordinator.Start(target.VisiblePages, new FrontendChangeStreamStart(RuntimeId, 0, false));

        source.Publish(1, FrontendChangeScope.Passwords);
        await delay.WaitForScheduledCountAsync(1);
        delay.AdvanceAll();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        target.VisiblePages = [FrontendRefreshPage.UserProfile];
        coordinator.OnNavigationChanged(target.VisiblePages);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(0, endpoints.CommittedCount);
        Assert.IsTrue(coordinator.IsDirty(FrontendRefreshPage.Passwords));
        Assert.IsFalse(coordinator.IsDirty(FrontendRefreshPage.UserProfile));
    }

    [TestMethod]
    public async Task ForeignUserHintDoesNotRefreshCurrentProfile()
    {
        var source = new FakeChangeHintSource();
        var delay = new FakeRefreshDelay();
        var endpoints = new FakeFrontendRefreshEndpointClient();
        var currentUser = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var target = new FakeFrontendRefreshTarget(endpoints)
        {
            VisiblePages = [FrontendRefreshPage.UserProfile],
            CurrentAuthenticationToken = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            CurrentUserId = currentUser
        };
        await using var coordinator = CreateCoordinator(source, target, delay);
        coordinator.OnAuthenticationChanged(target.CurrentAuthenticationToken, currentUser);
        coordinator.Start(target.VisiblePages, new FrontendChangeStreamStart(RuntimeId, 0, false));

        source.Publish(1, FrontendChangeScope.UserProfile,
            Guid.Parse("33333333-3333-3333-3333-333333333333"));
        await coordinator.WaitForIdleAsync();
        Assert.AreEqual(0, endpoints.Calls.Count);

        source.Publish(2, FrontendChangeScope.UserProfile, currentUser);
        await delay.WaitForScheduledCountAsync(1);
        delay.AdvanceAll();
        await endpoints.WaitForCallCountAsync(1);
        await coordinator.WaitForIdleAsync();

        Assert.AreEqual(FrontendRefreshPage.UserProfile, endpoints.Calls[0].Page);
    }

    [TestMethod]
    public async Task UnsafeEditorKeepsPageDirtyUntilItIsSafe()
    {
        var source = new FakeChangeHintSource();
        var delay = new FakeRefreshDelay();
        var endpoints = new FakeFrontendRefreshEndpointClient();
        var target = new FakeFrontendRefreshTarget(endpoints)
        {
            VisiblePages = [FrontendRefreshPage.Passwords],
            CanRefresh = false,
            UnsavedText = "draft stays here"
        };
        await using var coordinator = CreateCoordinator(source, target, delay);
        coordinator.Start(target.VisiblePages, new FrontendChangeStreamStart(RuntimeId, 0, false));

        source.Publish(1, FrontendChangeScope.Passwords);
        await delay.WaitForScheduledCountAsync(1);
        delay.AdvanceAll();
        await coordinator.WaitForIdleAsync();
        Assert.IsTrue(coordinator.IsDirty(FrontendRefreshPage.Passwords));
        Assert.AreEqual(0, endpoints.Calls.Count);

        target.CanRefresh = true;
        coordinator.OnPageStateChanged();
        await delay.WaitForScheduledCountAsync(2);
        delay.AdvanceAll();
        await endpoints.WaitForCallCountAsync(1);
        await coordinator.WaitForIdleAsync();

        Assert.AreEqual("draft stays here", target.UnsavedText);
        Assert.IsFalse(coordinator.IsDirty(FrontendRefreshPage.Passwords));
    }

    [TestMethod]
    public async Task FailedQueryLeavesDirtyStateAndLaterRetryCanClearIt()
    {
        var source = new FakeChangeHintSource();
        var delay = new FakeRefreshDelay();
        var endpoints = new FakeFrontendRefreshEndpointClient { Result = false };
        var target = new FakeFrontendRefreshTarget(endpoints)
        {
            VisiblePages = [FrontendRefreshPage.UserProfile]
        };
        await using var coordinator = CreateCoordinator(source, target, delay);
        coordinator.Start(target.VisiblePages, new FrontendChangeStreamStart(RuntimeId, 0, false));

        source.Publish(1, FrontendChangeScope.UserProfile);
        await delay.WaitForScheduledCountAsync(1);
        delay.AdvanceAll();
        await endpoints.WaitForCallCountAsync(1);
        await coordinator.WaitForIdleAsync();
        Assert.IsTrue(coordinator.IsDirty(FrontendRefreshPage.UserProfile));

        endpoints.Result = true;
        coordinator.OnPageStateChanged();
        await delay.WaitForScheduledCountAsync(2);
        delay.AdvanceAll();
        await endpoints.WaitForCallCountAsync(2);
        await coordinator.WaitForIdleAsync();
        Assert.IsFalse(coordinator.IsDirty(FrontendRefreshPage.UserProfile));
    }

    [TestMethod]
    public async Task FailedSessionRevalidationDoesNotBlockIndependentPages()
    {
        var source = new FakeChangeHintSource();
        var delay = new FakeRefreshDelay();
        var endpoints = new FakeFrontendRefreshEndpointClient();
        var target = new FakeFrontendRefreshTarget(endpoints)
        {
            VisiblePages = Enum.GetValues<FrontendRefreshPage>(),
            FailRevalidation = true
        };
        await using var coordinator = CreateCoordinator(source, target, delay);
        coordinator.Start(target.VisiblePages, new FrontendChangeStreamStart(RuntimeId, 0, true));

        await delay.WaitForScheduledCountAsync(1);
        delay.AdvanceAll();
        await endpoints.WaitForCallCountAsync(7);
        await coordinator.WaitForIdleAsync();

        Assert.AreEqual(Enum.GetValues<FrontendRefreshPage>().Length, endpoints.Calls.Count);
        Assert.IsFalse(coordinator.IsDirty(FrontendRefreshPage.Passwords));
        Assert.IsTrue(coordinator.IsDirty(FrontendRefreshPage.AuthenticationSessions));
    }

    [TestMethod]
    public async Task NewEventDuringRefreshSchedulesOnePendingRerun()
    {
        var source = new FakeChangeHintSource();
        var delay = new FakeRefreshDelay();
        var endpoints = new FakeFrontendRefreshEndpointClient();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rerunEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRerun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        endpoints.BeforeResult = async (call, cancellationToken) =>
        {
            if (call.Index == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            else if (call.Index == 2)
            {
                rerunEntered.TrySetResult();
                await releaseRerun.Task.WaitAsync(cancellationToken);
            }
        };
        var target = new FakeFrontendRefreshTarget(endpoints)
        {
            VisiblePages = [FrontendRefreshPage.Passwords]
        };
        await using var coordinator = CreateCoordinator(source, target, delay);
        coordinator.Start(target.VisiblePages, new FrontendChangeStreamStart(RuntimeId, 0, false));

        source.Publish(1, FrontendChangeScope.Passwords);
        await delay.WaitForScheduledCountAsync(1);
        delay.AdvanceAll();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        source.Publish(2, FrontendChangeScope.Passwords);
        await delay.WaitForScheduledCountAsync(2);
        delay.AdvanceAll();
        release.TrySetResult();
        await rerunEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(coordinator.IsDirty(FrontendRefreshPage.Passwords));
        releaseRerun.TrySetResult();
        await coordinator.WaitForIdleAsync();

        Assert.IsFalse(coordinator.IsDirty(FrontendRefreshPage.Passwords));
        Assert.AreEqual(1, endpoints.MaximumConcurrentCalls);
    }

    [TestMethod]
    public async Task RuntimeChangeCancelsOldGenerationAndRefreshesAllVisiblePages()
    {
        var source = new FakeChangeHintSource();
        var delay = new FakeRefreshDelay();
        var endpoints = new FakeFrontendRefreshEndpointClient();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRequestCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        endpoints.BeforeResult = async (call, cancellationToken) =>
        {
            if (call.Index == 1)
            {
                entered.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    oldRequestCancelled.TrySetResult();
                    throw;
                }
            }
        };
        var target = new FakeFrontendRefreshTarget(endpoints)
        {
            VisiblePages = Enum.GetValues<FrontendRefreshPage>()
        };
        await using var coordinator = CreateCoordinator(source, target, delay);
        coordinator.Start(target.VisiblePages, new FrontendChangeStreamStart(RuntimeId, 0, false));

        source.Publish(1, FrontendChangeScope.Passwords);
        await delay.WaitForScheduledCountAsync(1);
        delay.AdvanceAll();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var replacementRuntime = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        source.PublishMarker(0, replacementRuntime);
        await delay.WaitForScheduledCountAsync(2);
        delay.AdvanceAll();
        await oldRequestCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // Wait for the coordinator to drain the replacement generation before checking its calls.
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(8, endpoints.Calls.Count, "The cancelled request must be followed by all seven visible page refreshes.");
        Assert.AreEqual(7, endpoints.CommittedCount);
        Assert.AreEqual(1, target.RevalidationCount, "A replacement runtime marker must revalidate session state once.");
        Assert.AreEqual(1, endpoints.MaximumConcurrentCalls);
        Assert.AreEqual(
            Enum.GetValues<FrontendRefreshPage>().Length,
            endpoints.Calls.Skip(1).Select(call => call.Page).Distinct().Count());
    }

    [TestMethod]
    public async Task AuthenticationGenerationChangeDiscardsInFlightResult()
    {
        var source = new FakeChangeHintSource();
        var delay = new FakeRefreshDelay();
        var endpoints = new FakeFrontendRefreshEndpointClient();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        endpoints.BeforeResult = async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };
        var target = new FakeFrontendRefreshTarget(endpoints)
        {
            VisiblePages = [FrontendRefreshPage.Passwords],
            CurrentAuthenticationToken = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            CurrentUserId = Guid.Parse("22222222-2222-2222-2222-222222222222")
        };
        await using var coordinator = CreateCoordinator(source, target, delay);
        coordinator.OnAuthenticationChanged(target.CurrentAuthenticationToken, target.CurrentUserId);
        coordinator.Start(target.VisiblePages, new FrontendChangeStreamStart(RuntimeId, 0, false));

        source.Publish(1, FrontendChangeScope.Passwords);
        await delay.WaitForScheduledCountAsync(1);
        delay.AdvanceAll();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        target.CurrentAuthenticationToken = Guid.Parse("33333333-3333-3333-3333-333333333333");
        target.CurrentUserId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        coordinator.OnAuthenticationChanged(target.CurrentAuthenticationToken, target.CurrentUserId);
        release.TrySetResult();
        await coordinator.WaitForIdleAsync();

        Assert.AreEqual(0, endpoints.CommittedCount);
        Assert.IsFalse(coordinator.IsDirty(FrontendRefreshPage.Passwords));
    }

    [TestMethod]
    public async Task DisposalCancelsQueuedAndInFlightDelivery()
    {
        var source = new FakeChangeHintSource();
        var delay = new FakeRefreshDelay();
        var endpoints = new FakeFrontendRefreshEndpointClient();
        var target = new FakeFrontendRefreshTarget(endpoints)
        {
            VisiblePages = [FrontendRefreshPage.Passwords]
        };
        var coordinator = CreateCoordinator(source, target, delay);
        coordinator.Start(target.VisiblePages, new FrontendChangeStreamStart(RuntimeId, 0, false));
        source.Publish(1, FrontendChangeScope.Passwords);
        await delay.WaitForScheduledCountAsync(1);

        await coordinator.DisposeAsync();
        delay.AdvanceAll();
        source.Publish(2, FrontendChangeScope.Passwords);

        Assert.AreEqual(0, endpoints.Calls.Count);
        Assert.IsTrue(coordinator.IsDisposed);

        var activeSource = new FakeChangeHintSource();
        var activeDelay = new FakeRefreshDelay();
        var activeEndpoints = new FakeFrontendRefreshEndpointClient();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activeEndpoints.BeforeResult = async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };
        var activeTarget = new FakeFrontendRefreshTarget(activeEndpoints)
        {
            VisiblePages = [FrontendRefreshPage.Passwords]
        };
        var activeCoordinator = CreateCoordinator(activeSource, activeTarget, activeDelay);
        activeCoordinator.Start(activeTarget.VisiblePages, new FrontendChangeStreamStart(RuntimeId, 0, false));
        activeSource.Publish(1, FrontendChangeScope.Passwords);
        await activeDelay.WaitForScheduledCountAsync(1);
        activeDelay.AdvanceAll();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await activeCoordinator.DisposeAsync();

        Assert.AreEqual(0, activeEndpoints.CommittedCount);
    }

    private static FrontendRefreshCoordinator CreateCoordinator(
        FakeChangeHintSource source,
        FakeFrontendRefreshTarget target,
        FakeRefreshDelay delay) =>
        new(source, target, target.Dispatcher, delay, TimeSpan.FromMilliseconds(200));

    private sealed class FakeChangeHintSource : IFrontendChangeHintSource
    {
        public event Action<FrontendChangeStreamDelivery>? ChangeHintReceived;

        public void Publish(long sequence, FrontendChangeScope scope, Guid? userId = null) =>
            ChangeHintReceived?.Invoke(new FrontendChangeStreamDelivery(
                RuntimeId,
                sequence,
                new FrontendChangeNotification(RuntimeId, sequence, scope, userId, DateTimeOffset.UnixEpoch),
                RequiresAuthoritativeRefresh: false));

        public void PublishMarker(long sequence, Guid runtimeId) =>
            ChangeHintReceived?.Invoke(new FrontendChangeStreamDelivery(
                runtimeId,
                sequence,
                Notification: null,
                RequiresAuthoritativeRefresh: true));
    }

    private sealed class FakeRefreshDispatcher : IFrontendRefreshDispatcher
    {
        public int DispatchCount { get; private set; }

        public Task<T> InvokeAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DispatchCount++;
            return operation();
        }
    }

    private sealed class FakeRefreshDelay : IFrontendRefreshDelay
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _pending = [];
        private readonly SemaphoreSlim _scheduled = new(0);
        private int _scheduledCount;

        public int ScheduledCount
        {
            get { lock (_gate) return _scheduledCount; }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _pending.Add(completion);
                _scheduledCount++;
            }
            _scheduled.Release();
            return completion.Task.WaitAsync(cancellationToken);
        }

        public async Task WaitForScheduledCountAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                lock (_gate)
                {
                    if (_scheduledCount >= count)
                        return;
                }
                await _scheduled.WaitAsync(timeout.Token);
            }
        }

        public void AdvanceAll()
        {
            TaskCompletionSource[] pending;
            lock (_gate)
            {
                pending = _pending.ToArray();
                _pending.Clear();
            }
            foreach (var completion in pending)
                completion.TrySetResult();
        }
    }

    private sealed class FakeFrontendRefreshTarget(FakeFrontendRefreshEndpointClient endpoints)
        : IFrontendRefreshTarget
    {
        public FakeRefreshDispatcher Dispatcher { get; } = new();
        public bool IsActive { get; set; } = true;
        public Guid CurrentAuthenticationToken { get; set; }
        public Guid? CurrentUserId { get; set; }
        public IReadOnlyCollection<FrontendRefreshPage> VisiblePages { get; set; } = [];
        public bool CanRefresh { get; set; } = true;
        public bool FailRevalidation { get; set; }
        public string UnsavedText { get; set; } = string.Empty;
        public int RevalidationCount { get; private set; }

        public IReadOnlyCollection<FrontendRefreshPage> GetVisiblePages() => VisiblePages;

        public bool CanRefreshPage(FrontendRefreshPage page) => CanRefresh;

        public async Task<bool> RefreshPageAsync(
            FrontendRefreshPage page,
            FrontendChangeScope scopes,
            Func<bool> isCurrent,
            CancellationToken cancellationToken)
        {
            if (!isCurrent())
                return false;
            var result = await endpoints.RefreshAsync(page, scopes, cancellationToken);
            return result && isCurrent();
        }

        public Task<bool> RevalidateCurrentSessionAsync(
            Func<bool> isCurrent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RevalidationCount++;
            if (FailRevalidation)
                return Task.FromException<bool>(new InvalidOperationException("session status failed"));
            return Task.FromResult(isCurrent());
        }
    }

    private sealed class FakeFrontendRefreshEndpointClient
    {
        private readonly ConcurrentQueue<EndpointCall> _calls = new();
        private readonly SemaphoreSlim _callSignal = new(0);
        private int _activeCalls;
        private int _maximumConcurrentCalls;
        private int _committedCount;

        public IReadOnlyList<EndpointCall> Calls => _calls.ToArray();
        public bool Result { get; set; } = true;
        public int MaximumConcurrentCalls => Volatile.Read(ref _maximumConcurrentCalls);
        public int CommittedCount => Volatile.Read(ref _committedCount);
        public Func<EndpointCall, CancellationToken, Task>? BeforeResult { get; set; }

        public async Task<bool> RefreshAsync(
            FrontendRefreshPage page,
            FrontendChangeScope scopes,
            CancellationToken cancellationToken)
        {
            var index = _calls.Count + 1;
            var call = new EndpointCall(index, page, scopes);
            _calls.Enqueue(call);
            _callSignal.Release();
            var active = Interlocked.Increment(ref _activeCalls);
            UpdateMaximum(active);
            try
            {
                if (BeforeResult is { } beforeResult)
                    await beforeResult(call, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (Result)
                    Interlocked.Increment(ref _committedCount);
                return Result;
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        public async Task WaitForCallCountAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (_calls.Count < count)
                await _callSignal.WaitAsync(timeout.Token);
        }

        private void UpdateMaximum(int active)
        {
            while (active > Volatile.Read(ref _maximumConcurrentCalls))
            {
                var current = Volatile.Read(ref _maximumConcurrentCalls);
                if (active <= current || Interlocked.CompareExchange(ref _maximumConcurrentCalls, active, current) == current)
                    return;
            }
        }
    }

    private sealed record EndpointCall(int Index, FrontendRefreshPage Page, FrontendChangeScope Scopes);
}
