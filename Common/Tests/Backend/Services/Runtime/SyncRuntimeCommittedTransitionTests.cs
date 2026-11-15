using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Services.Hosted;
using PasswordManagerLocal.Common.Backend.State;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

public sealed partial class SyncRuntimeCommittedTransitionTests
{
    [TestMethod]
    public async Task RapidCommittedTogglesAndDuplicateWakeups_ReadFinalDatabaseState_WithoutRepeatedStarts()
    {
        var activity = new RevocationDeliveryActivity();
        await using var db = await SyncRuntimeEligibilityTests.Fixture.CreateAsync(activity);
        var (user, peer) = await db.AddProfileAsync();
        using var provider = CreateCommittedStateProvider(db, activity);
        var calls = new List<string>();
        var runtime = CreateCommittedStateRuntime(db, provider, calls);
        var clock = new ControlledDelayTimeProvider();
        var supervisor = new RevocationDeliveryRuntimeHostedService(activity, runtime, clock);
        await supervisor.StartAsync();
        try
        {
            (await clock.NextAsync(TimeSpan.FromMilliseconds(75))).Fire();
            _ = await clock.NextAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(SyncRuntimeState.Running, runtime.Snapshot.State);
            for (var index = 0; index < 21; index++)
            {
                var link = (await db.Db.UserDevices.GetAsync(user.UId, peer!.Id))!;
                link.IsSyncOn = index % 2 != 0;
                db.Db.UserDevices.Update(link);
                await db.Db.UnitOfWork.SaveChangesAsync();
                activity.RequestRefresh();
            }
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.StateChanged += (_, args) => { if (args.Current.State == SyncRuntimeState.Disabled) idle.TrySetResult(); };
            (await clock.NextAsync(TimeSpan.FromMilliseconds(75))).Fire();
            await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, calls.Count(call => call == "start:network"));
            Assert.IsFalse((await db.EvaluateAsync()).IsEligible);
            var final = (await db.Db.UserDevices.GetAsync(user.UId, peer!.Id))!;
            final.IsSyncOn = true;
            db.Db.UserDevices.Update(final);
            await db.Db.UnitOfWork.SaveChangesAsync();
            for (var pass = 0; ; pass++)
            {
                Assert.IsTrue(pass < 8, "Duplicate notifications must settle after a bounded number of reconciliations.");
                var next = await clock.NextAsync();
                if (next.DueTime == TimeSpan.FromSeconds(30)) break;
                Assert.AreEqual(TimeSpan.FromMilliseconds(75), next.DueTime);
                next.Fire();
            }
            Assert.AreEqual(SyncRuntimeState.Running, runtime.Snapshot.State);
            Assert.AreEqual(2, calls.Count(call => call == "start:network"));
        }
        finally { await supervisor.StopAsync(); await runtime.StopAsync(); }
    }

    [TestMethod]
    public async Task StartupAfterPostCommitNotificationFailure_ReconstructsEligibilityFromDurableRows()
    {
        var activity = new FailingRefreshActivity();
        await using var db = await SyncRuntimeEligibilityTests.Fixture.CreateAsync(activity);
        using var provider = CreateCommittedStateProvider(db, activity);
        var calls = new List<string>();
        var previous = CreateCommittedStateRuntime(db, provider, calls);
        await previous.RefreshSyncEnabledAsync();
        Assert.AreEqual(SyncRuntimeState.Disabled, previous.Snapshot.State);
        activity.Fail = true;
        await Assert.ThrowsExactlyAsync<IOException>(async () => { await db.AddProfileAsync(); });
        activity.Fail = false;
        using (var readback = db.Db.OpenSession())
            Assert.IsTrue((await new SyncRuntimeEligibilityRepository(readback, db.Identity).EvaluateAsync()).IsEligible,
                "A callback failure after COMMIT cannot erase the enrolled relationship.");
        await previous.StopAsync();
        var restarted = CreateCommittedStateRuntime(db, provider, calls);
        try
        {
            await restarted.StartAsync();
            Assert.AreEqual(SyncRuntimeState.Running, restarted.Snapshot.State);
            Assert.AreEqual(SyncRuntimeReason.NormalProfileSynchronization, restarted.Snapshot.Reasons);
            Assert.AreEqual(1, calls.Count(call => call == "start:network"));
        }
        finally { await restarted.StopAsync(); }
    }

    private static ServiceProvider CreateCommittedStateProvider(SyncRuntimeEligibilityTests.Fixture db, IRevocationDeliveryActivity activity)
    {
        var services = new ServiceCollection();
        services.AddScoped<SqliteSession>(_ => new(db.Db.Connections));
        services.AddScoped<ISyncRuntimeEligibilityService>(sp => new SyncRuntimeEligibilityRepository(sp.GetRequiredService<SqliteSession>(), db.Identity));
        services.AddSingleton(activity);
        return services.BuildServiceProvider();
    }

    private static SyncRuntimeService CreateCommittedStateRuntime(SyncRuntimeEligibilityTests.Fixture db, ServiceProvider provider, List<string> calls) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), db.Identity, new EnrollmentRuntimeState(),
            new FakeSyncDeviceIdentityService(), new DiscoveredDeviceEndpointRegistry(), new FakeDeviceSyncTaskService(),
            [new FakeControlledHostedService("network", 1, calls)]);

    private sealed class FailingRefreshActivity : IRevocationDeliveryActivity
    {
        public bool Fail { get; set; }
        public bool HasActiveWork => false;
        public IDisposable Enter() => throw new NotSupportedException();
        public void RequestRefresh() { if (Fail) throw new IOException("Injected post-commit notification failure."); }
        public IAsyncEnumerable<bool> ReadRefreshesAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
