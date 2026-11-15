using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Services.Hosted;
using PasswordManagerLocal.Common.Backend.State;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
[TestCategory("Backend")]
[TestCategory("Unit")]
public sealed class SyncRuntimeResilienceTests
{
    [TestMethod]
    public async Task IdleHasNoNetworkWork_EnrollmentStillWorks_AndUnchangedEligibleStateDoesNotRestart()
    {
        using var fixture = new Fixture();
        await fixture.Runtime.RefreshSyncEnabledAsync();
        Assert.AreEqual(SyncRuntimeState.Disabled, fixture.Runtime.Snapshot.State);
        Assert.IsTrue(fixture.Runtime.IsHealthy);
        Assert.AreEqual(0, fixture.Services.Sum(service => service.Starts));
        await fixture.Runtime.BeginEnrollmentOnlyAsync();
        Assert.IsFalse(fixture.Identity.IsSyncOn);
        Assert.AreEqual(SyncRuntimeReason.ActiveEnrollment, fixture.Runtime.Snapshot.Reasons);
        await fixture.Runtime.EndEnrollmentOnlyAsync();
        Assert.AreEqual(SyncRuntimeState.Disabled, fixture.Runtime.Snapshot.State);
        fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.Eligible);
        await fixture.Runtime.RefreshSyncEnabledAsync();
        var starts = fixture.Services.Sum(service => service.Starts);
        for (var index = 0; index < 20; index++) await fixture.Runtime.RefreshSyncEnabledAsync();
        Assert.AreEqual(starts, fixture.Services.Sum(service => service.Starts));
        Assert.AreEqual(SyncRuntimeReason.NormalProfileSynchronization, fixture.Runtime.Snapshot.Reasons);
        await fixture.Runtime.StopAsync();
    }

    [TestMethod]
    public async Task LastEligibleRelationshipWaitsForInFlightWork_AndDeliveryIsIndependentOfNormalSettings()
    {
        using var fixture = new Fixture();
        fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.Eligible);
        await fixture.Runtime.RefreshSyncEnabledAsync();
        using (fixture.Activity.Enter())
        {
            fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.NoEligibleRemoteDevices);
            await fixture.Runtime.RefreshSyncEnabledAsync();
            Assert.AreEqual(SyncRuntimeReason.ActiveInFlightSynchronization, fixture.Runtime.Snapshot.Reasons);
            Assert.AreEqual(SyncRuntimeState.Running, fixture.Runtime.Snapshot.State);
        }
        fixture.Delivery.Pending = true;
        await fixture.Runtime.RefreshSyncEnabledAsync();
        Assert.AreEqual(SyncRuntimeReason.PendingRevocationDelivery, fixture.Runtime.Snapshot.Reasons);
        fixture.Delivery.Pending = false;
        fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.Eligible);
        await fixture.Runtime.RefreshSyncEnabledAsync();
        Assert.AreEqual(SyncRuntimeState.Running, fixture.Runtime.Snapshot.State, "Final receipt cannot stop an eligible normal pair.");
        fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.NoLocalProfiles);
        await fixture.Runtime.RefreshSyncEnabledAsync();
        Assert.AreEqual(SyncRuntimeState.Disabled, fixture.Runtime.Snapshot.State);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task FirstOrPartialStartupFailure_CleansUpBeforeRetrying(int failingService)
    {
        using var fixture = new Fixture();
        fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.Eligible);
        fixture.Services[failingService].FailuresRemaining = 1;
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Runtime.RefreshSyncEnabledAsync());
        Assert.AreEqual(SyncRuntimeState.Degraded, fixture.Runtime.Snapshot.State);
        Assert.IsTrue(fixture.Runtime.Snapshot.IsRetryable);
        Assert.IsFalse(fixture.Runtime.Snapshot.RequiresProcessRestart);
        for (var index = 0; index <= failingService; index++) Assert.AreEqual(1, fixture.Services[index].Stops);
        await fixture.Runtime.RefreshSyncEnabledAsync();
        Assert.AreEqual(SyncRuntimeState.Running, fixture.Runtime.Snapshot.State);
        Assert.IsTrue(fixture.Services.All(service => service.IsHealthy));
        await fixture.Runtime.StopAsync();
    }

    [TestMethod]
    public async Task AutomaticRetryNeedsNoFrontend_AndFallbackDetectsARequiredTaskTerminating()
    {
        using var fixture = new Fixture();
        fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.Eligible);
        fixture.Services[2].FailuresRemaining = 1;
        var supervisor = new RevocationDeliveryRuntimeHostedService(fixture.Activity, fixture.Runtime, new AcceleratedClock());
        await supervisor.StartAsync();
        try
        {
            await UntilAsync(() => fixture.Runtime.Snapshot.State == SyncRuntimeState.Running);
            Assert.AreEqual(2, fixture.Services[2].Starts);
            fixture.Services[1].IsHealthy = false; // No state event: the fallback observes the terminated task.
            await UntilAsync(() => fixture.Services[1].Starts == 3 && fixture.Runtime.Snapshot.State == SyncRuntimeState.Running);
            Assert.IsTrue(fixture.Services.All(service => service.MaximumConcurrentStarts == 1));
        }
        finally { await supervisor.StopAsync(); await fixture.Runtime.StopAsync(); }
    }

    [TestMethod]
    public async Task UnsafeShutdownLatchesRestartRequired_AndNeverAutomaticallyReopensResources()
    {
        using var fixture = new Fixture();
        fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.Eligible);
        await fixture.Runtime.RefreshSyncEnabledAsync();
        fixture.Services[1].FailStop = true;
        fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.NoLocalProfiles);
        await Assert.ThrowsExactlyAsync<AggregateException>(() => fixture.Runtime.RefreshSyncEnabledAsync());
        Assert.IsTrue(fixture.Runtime.Snapshot.RequiresProcessRestart);
        Assert.IsFalse(fixture.Runtime.Snapshot.IsRetryable);
        fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.Eligible);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Runtime.RefreshSyncEnabledAsync());
        Assert.IsTrue(fixture.Services.All(service => service.Starts == 1));
        var supervisor = new RevocationDeliveryRuntimeHostedService(fixture.Activity, fixture.Runtime, new AcceleratedClock());
        await supervisor.StartAsync();
        try
        {
            for (var index = 0; index < 50; index++) fixture.Activity.RequestRefresh();
            await Task.Delay(100);
            Assert.IsTrue(fixture.Services.All(service => service.Starts == 1));
        }
        finally { await supervisor.StopAsync(); }
        fixture.Services[1].FailStop = false;
        await fixture.Runtime.StopAsync();
        Assert.IsTrue(fixture.Runtime.Snapshot.RequiresProcessRestart, "Cleanup cannot clear the process-restart latch.");
    }

    [TestMethod]
    public async Task CoalescedCommittedChangesReactivateIdleRuntime_AndShutdownCancelsAllRetries()
    {
        using var fixture = new Fixture();
        var supervisor = new RevocationDeliveryRuntimeHostedService(fixture.Activity, fixture.Runtime, new AcceleratedClock());
        await supervisor.StartAsync();
        try
        {
            await UntilAsync(() => fixture.Runtime.Snapshot.State == SyncRuntimeState.Disabled);
            var reads = fixture.Eligibility.Calls;
            await Task.Delay(100);
            Assert.AreEqual(reads, fixture.Eligibility.Calls, "An idle runtime has no periodic database reconciliation.");
            fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.Eligible);
            for (var index = 0; index < 200; index++) fixture.Activity.RequestRefresh();
            await UntilAsync(() => fixture.Runtime.Snapshot.State == SyncRuntimeState.Running);
            Assert.IsTrue(fixture.Services.All(service => service.Starts == 1));
            fixture.Eligibility.Handler = _ => throw new IOException("transient eligibility read");
            fixture.Activity.RequestRefresh();
            await UntilAsync(() => fixture.Runtime.Snapshot.State == SyncRuntimeState.Degraded);
        }
        finally { await supervisor.StopAsync(); }
        var finalReads = fixture.Eligibility.Calls;
        await Task.Delay(100);
        Assert.AreEqual(finalReads, fixture.Eligibility.Calls);
        fixture.Eligibility.Handler = null;
        await fixture.Runtime.StopAsync();
    }

    [TestMethod]
    public async Task CommittedMutationSurvivesFailedRead_AndRelevantEventCanBringRetryForward()
    {
        using var fixture = new Fixture();
        var first = true;
        fixture.Eligibility.Handler = _ => first
            ? Task.FromException<SyncRuntimeEligibilityResult>(new IOException("temporary read failure"))
            : Task.FromResult(new SyncRuntimeEligibilityResult(SyncRuntimeEligibilityReason.Eligible));
        var supervisor = new RevocationDeliveryRuntimeHostedService(fixture.Activity, fixture.Runtime);
        await supervisor.StartAsync();
        try
        {
            await UntilAsync(() => fixture.Runtime.Snapshot.State == SyncRuntimeState.Degraded);
            first = false;
            fixture.Activity.RequestRefresh();
            await UntilAsync(() => fixture.Runtime.Snapshot.State == SyncRuntimeState.Running);
        }
        finally { await supervisor.StopAsync(); await fixture.Runtime.StopAsync(); }
    }

    [TestMethod]
    public async Task PassiveHistoricalIngressHasIndependentOwnership_AndNoNormalWorkers()
    {
        var passive = new PassiveRuntime();
        using var fixture = new Fixture(passive);
        fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.NoLocalProfiles, HasHistoricalControlEvidence: true);
        await fixture.Runtime.RefreshSyncEnabledAsync();
        Assert.AreEqual(SyncRuntimeState.Disabled, fixture.Runtime.Snapshot.State);
        Assert.IsTrue(fixture.Runtime.Snapshot.IsPassiveListening);
        Assert.IsFalse(fixture.Identity.IsSyncOn);
        Assert.IsTrue(fixture.Services.All(service => service.Starts == 0));
        await fixture.Runtime.RefreshSyncEnabledAsync();
        Assert.AreEqual(1, passive.Starts);
        passive.IsHealthy = false;
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Runtime.RefreshSyncEnabledAsync());
        Assert.IsTrue(fixture.Runtime.Snapshot.IsRetryable);
        await fixture.Runtime.RefreshSyncEnabledAsync();
        Assert.IsTrue(fixture.Runtime.Snapshot.IsPassiveListening);
        fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.NoLocalProfiles);
        await fixture.Runtime.RefreshSyncEnabledAsync();
        Assert.IsFalse(fixture.Runtime.Snapshot.IsPassiveListening);
        Assert.AreEqual(2, passive.Stops);
    }

    private sealed class PassiveRuntime : IPassiveSyncRuntime
    {
        public bool IsHealthy { get; set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public Task StartAsync(CancellationToken ct = default) { Starts++; IsHealthy = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct = default) { Stops++; IsHealthy = false; return Task.CompletedTask; }
    }

    [TestMethod]
    public void RetryDelayIsExponentialAndBounded()
    {
        CollectionAssert.AreEqual(new[] { 1d, 2d, 4d, 8d, 16d, 32d, 60d, 60d },
            Enumerable.Range(1, 8).Select(attempt => RevocationDeliveryRuntimeHostedService.RetryDelay(attempt).TotalSeconds).ToArray());
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }

    private sealed class AcceleratedClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            TimeProvider.System.CreateTimer(callback, state, Scale(dueTime), Scale(period));
        private static TimeSpan Scale(TimeSpan value) => value == Timeout.InfiniteTimeSpan ? value
            : TimeSpan.FromMilliseconds(Math.Max(1, value.TotalMilliseconds / 100));
    }

    private sealed class Fixture : IDisposable
    {
        public FakeSyncRuntimeEligibilityService Eligibility { get; } = new();
        public FakeDeviceIdentityService Identity { get; } = new() { IsSyncOn = false };
        public RevocationDeliveryActivity Activity { get; } = new();
        public FakeRevocationDeliveryService Delivery { get; } = new();
        public FaultingService[] Services { get; } = Enumerable.Range(0, 4).Select(index => new FaultingService(index * 10)).ToArray();
        public SyncRuntimeService Runtime { get; }
        private readonly ServiceProvider _provider;
        public Fixture(IPassiveSyncRuntime? passive = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton<ISyncRuntimeEligibilityService>(Eligibility);
            services.AddSingleton<IRevocationDeliveryActivity>(Activity);
            services.AddSingleton<IRevocationDeliveryService>(Delivery);
            _provider = services.BuildServiceProvider();
            Runtime = new SyncRuntimeService(_provider.GetRequiredService<IServiceScopeFactory>(), Identity,
                new EnrollmentRuntimeState(), new FakeSyncDeviceIdentityService(), new DiscoveredDeviceEndpointRegistry(),
                new FakeDeviceSyncTaskService(), Services, passive: passive);
        }
        public void Dispose() => _provider.Dispose();
    }

    private sealed class FaultingService(int order) : ISyncControlledHostedService, ISyncRuntimeHealth
    {
        public int StartOrder => order;
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public int FailuresRemaining { get; set; }
        public bool FailStop { get; set; }
        public bool IsHealthy { get; set; }
        public int MaximumConcurrentStarts { get; private set; }
        private int _starting;
        public async Task StartAsync(CancellationToken ct)
        {
            var active = Interlocked.Increment(ref _starting);
            MaximumConcurrentStarts = Math.Max(active, MaximumConcurrentStarts);
            try
            {
                Starts++;
                await Task.Yield();
                if (FailuresRemaining-- > 0) throw new IOException("temporary controlled-service failure");
                IsHealthy = true;
            }
            finally { Interlocked.Decrement(ref _starting); }
        }
        public Task StopAsync(CancellationToken ct)
        {
            Stops++;
            if (FailStop) throw new IOException("uncertain resource ownership");
            IsHealthy = false;
            return Task.CompletedTask;
        }
    }

}
