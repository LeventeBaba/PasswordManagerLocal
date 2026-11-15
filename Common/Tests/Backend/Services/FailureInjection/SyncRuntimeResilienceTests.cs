using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services.Hosted;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

public sealed partial class SyncRuntimeResilienceTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(75);

    [TestMethod]
    public async Task CancellationDuringPartialStartup_CleansStartedOwners_AndNextRefreshUsesFreshEligibility()
    {
        using var fixture = new Fixture();
        fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.Eligible);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Services[2].StartEntered = entered;
        fixture.Services[2].StartRelease = release.Task;
        using var cancellation = new CancellationTokenSource();
        var startup = fixture.Runtime.RefreshSyncEnabledAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => startup);
            Assert.IsTrue(fixture.Services.Take(3).All(service => service.Stops == 1 && !service.IsHealthy));
            Assert.AreEqual(0, fixture.Services[3].Starts);
            Assert.IsFalse(fixture.Runtime.Snapshot.RequiresProcessRestart);
            fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.NoEligibleRemoteDevices);
            await fixture.Runtime.RefreshSyncEnabledAsync();
            Assert.AreEqual(SyncRuntimeState.Disabled, fixture.Runtime.Snapshot.State);
            Assert.IsTrue(fixture.Services.All(service => service.Starts <= 1));
        }
        finally
        {
            cancellation.Cancel(); release.TrySetResult();
            try { await startup; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            await fixture.Runtime.StopAsync();
        }
    }

    [TestMethod]
    public async Task PassivePartialStartupFailure_IsCleanedBeforeRetry_WithoutEnablingOrdinaryNetworking()
    {
        var passive = new PassiveRuntime { FailuresRemaining = 1 };
        using var fixture = new Fixture(passive);
        fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.NoLocalProfiles, HasHistoricalControlEvidence: true);
        try
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Runtime.RefreshSyncEnabledAsync());
            Assert.IsTrue(fixture.Runtime.Snapshot.IsRetryable);
            Assert.AreEqual(1, passive.Stops);
            Assert.IsFalse(passive.IsHealthy);
            await fixture.Runtime.RefreshSyncEnabledAsync();
            Assert.AreEqual(2, passive.Starts);
            Assert.AreEqual(SyncRuntimeState.Disabled, fixture.Runtime.Snapshot.State);
            Assert.IsTrue(fixture.Runtime.Snapshot.IsPassiveListening);
            Assert.IsFalse(fixture.Identity.IsSyncOn);
            Assert.IsTrue(fixture.Services.All(service => service.Starts == 0));
        }
        finally { await fixture.Runtime.StopAsync(); }
    }

    [TestMethod]
    public async Task UncertainPassiveShutdown_LatchesRestartAndCannotCreateAnotherListener()
    {
        var passive = new PassiveRuntime();
        using var fixture = new Fixture(passive);
        fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.NoLocalProfiles, HasHistoricalControlEvidence: true);
        await fixture.Runtime.RefreshSyncEnabledAsync();
        try
        {
            passive.FailStop = true;
            fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.NoLocalProfiles);
            await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Runtime.RefreshSyncEnabledAsync());
            Assert.IsTrue(fixture.Runtime.Snapshot.RequiresProcessRestart);
            Assert.IsFalse(fixture.Runtime.Snapshot.IsRetryable);
            fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.NoLocalProfiles, HasHistoricalControlEvidence: true);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Runtime.RefreshSyncEnabledAsync());
            Assert.AreEqual(1, passive.Starts);
        }
        finally { passive.FailStop = false; await fixture.Runtime.StopAsync(); }
        Assert.IsTrue(fixture.Runtime.Snapshot.RequiresProcessRestart);
    }

    [TestMethod]
    public async Task UnhealthyRequiredTask_IsDetectedByFallback_AndCleanedBeforeTimedRetry()
    {
        using var fixture = new Fixture();
        fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.Eligible);
        var clock = new ControlledDelayTimeProvider();
        var supervisor = new RevocationDeliveryRuntimeHostedService(fixture.Activity, fixture.Runtime, clock);
        await supervisor.StartAsync();
        try
        {
            (await clock.NextAsync(Debounce)).Fire();
            var fallback = await clock.NextAsync(TimeSpan.FromSeconds(30));
            fixture.Services[1].IsHealthy = false;
            fallback.Fire();
            var retry = await clock.NextAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(SyncRuntimeState.Degraded, fixture.Runtime.Snapshot.State);
            Assert.IsTrue(fixture.Services.All(service => service.Stops == 1 && !service.IsHealthy));
            retry.Fire();
            _ = await clock.NextAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(SyncRuntimeState.Running, fixture.Runtime.Snapshot.State);
            Assert.IsTrue(fixture.Services.All(service => service.Starts == 2 && service.MaximumConcurrentStarts == 1));
        }
        finally { await supervisor.StopAsync(); await fixture.Runtime.StopAsync(); }
    }

    [TestMethod]
    public async Task Supervisor_RetriesOnlyWhenBackoffExpires_AndHealthyFallbackDoesNotReadSqlite()
    {
        using var fixture = new Fixture();
        fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.Eligible);
        fixture.Services[3].FailuresRemaining = 2;
        var clock = new ControlledDelayTimeProvider();
        var supervisor = new RevocationDeliveryRuntimeHostedService(fixture.Activity, fixture.Runtime, clock);
        await supervisor.StartAsync();
        try
        {
            (await clock.NextAsync(Debounce)).Fire();
            var firstRetry = await clock.NextAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(SyncRuntimeState.Degraded, fixture.Runtime.Snapshot.State);
            Assert.AreEqual(1, fixture.Eligibility.Calls);
            Assert.IsTrue(fixture.Services.All(service => service.Stops == 1 && !service.IsHealthy));
            firstRetry.Fire();
            var secondRetry = await clock.NextAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(2, fixture.Eligibility.Calls);
            Assert.IsTrue(fixture.Services.All(service => service.Starts == 2 && service.Stops == 2));
            secondRetry.Fire();
            var healthCheck = await clock.NextAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(SyncRuntimeState.Running, fixture.Runtime.Snapshot.State);
            var reads = fixture.Eligibility.Calls;
            healthCheck.Fire();
            _ = await clock.NextAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(reads, fixture.Eligibility.Calls);
            Assert.IsTrue(fixture.Services.All(service => service.Starts == 3 && service.MaximumConcurrentStarts == 1));
        }
        finally { await supervisor.StopAsync(); await fixture.Runtime.StopAsync(); }
        Assert.AreEqual(0, clock.ActiveTimers);
    }

    [TestMethod]
    public async Task Supervisor_ShutdownDuringBackoffCancelsTimer_AndCannotStartAnotherGeneration()
    {
        using var fixture = new Fixture();
        fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.Eligible);
        fixture.Services[0].FailuresRemaining = 10;
        var clock = new ControlledDelayTimeProvider();
        var supervisor = new RevocationDeliveryRuntimeHostedService(fixture.Activity, fixture.Runtime, clock);
        await supervisor.StartAsync();
        try
        {
            (await clock.NextAsync(Debounce)).Fire();
            var retry = await clock.NextAsync(TimeSpan.FromSeconds(1));
            await supervisor.StopAsync();
            Assert.IsTrue(retry.IsDisposed);
            retry.Fire();
            fixture.Activity.RequestRefresh();
            Assert.AreEqual(1, fixture.Eligibility.Calls);
            Assert.AreEqual(1, fixture.Services[0].Starts);
            Assert.AreEqual(0, clock.ActiveTimers);
        }
        finally { await supervisor.StopAsync(); await fixture.Runtime.StopAsync(); }
    }

    [TestMethod]
    public async Task Supervisor_RemovingLastReasonDuringBackoffBecomesIdleWithoutRestartingResources()
    {
        using var fixture = new Fixture();
        fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.Eligible);
        fixture.Services[1].FailuresRemaining = 1;
        var clock = new ControlledDelayTimeProvider();
        var supervisor = new RevocationDeliveryRuntimeHostedService(fixture.Activity, fixture.Runtime, clock);
        await supervisor.StartAsync();
        try
        {
            (await clock.NextAsync(Debounce)).Fire();
            var retry = await clock.NextAsync(TimeSpan.FromSeconds(1));
            fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.NoEligibleRemoteDevices);
            fixture.Activity.RequestRefresh();
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Runtime.StateChanged += (_, args) =>
            {
                if (args.Current.State == SyncRuntimeState.Disabled) idle.TrySetResult();
            };
            (await clock.NextAsync(Debounce)).Fire();
            await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(retry.IsDisposed);
            Assert.AreEqual(SyncRuntimeState.Disabled, fixture.Runtime.Snapshot.State);
            Assert.IsTrue(fixture.Services.All(service => service.Starts <= 1));
        }
        finally { await supervisor.StopAsync(); await fixture.Runtime.StopAsync(); }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RefreshDuringStartupOrShutdown_RechecksLatestEligibilityWithoutOverlappingOwners(bool duringStartup)
    {
        using var fixture = new Fixture();
        fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.Eligible);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task transition;
        if (duringStartup)
        {
            fixture.Services[2].StartEntered = entered;
            fixture.Services[2].StartRelease = release.Task;
            transition = fixture.Runtime.RefreshSyncEnabledAsync();
        }
        else
        {
            await fixture.Runtime.RefreshSyncEnabledAsync();
            fixture.Services[2].StopEntered = entered;
            fixture.Services[2].StopRelease = release.Task;
            fixture.Eligibility.Result = new(SyncRuntimeEligibilityReason.NoEligibleRemoteDevices);
            transition = fixture.Runtime.RefreshSyncEnabledAsync();
        }
        Task? latest = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Eligibility.Result = new(duringStartup ? SyncRuntimeEligibilityReason.NoEligibleRemoteDevices : SyncRuntimeEligibilityReason.Eligible);
            latest = fixture.Runtime.RefreshSyncEnabledAsync();
            Assert.IsFalse(latest.IsCompleted, "A second reconciliation must wait for the current resource owner.");
            release.TrySetResult();
            await Task.WhenAll(transition, latest).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(duringStartup ? SyncRuntimeState.Disabled : SyncRuntimeState.Running, fixture.Runtime.Snapshot.State);
            Assert.IsTrue(fixture.Services.All(service => service.MaximumConcurrentStarts == 1));
        }
        finally
        {
            release.TrySetResult();
            await transition;
            if (latest is not null) await latest;
            await fixture.Runtime.StopAsync();
        }
    }
}
