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

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
[TestCategory("Backend")]
[TestCategory("Integration")]
public sealed class SyncRuntimeCommittedTransitionTests
{
    [TestMethod]
    public async Task CommittedRemoteSettingsSecurityAndMembershipChanges_WakeRuntimeWithoutFrontendRefresh()
    {
        var activity = new RevocationDeliveryActivity();
        await using var db = await SyncRuntimeEligibilityTests.Fixture.CreateAsync(activity);
        var (user, peer) = await db.AddProfileAsync();
        var services = new ServiceCollection();
        services.AddScoped<SqliteSession>(_ => new(db.Db.Connections));
        services.AddScoped<ISyncRuntimeEligibilityService>(sp => new SyncRuntimeEligibilityRepository(sp.GetRequiredService<SqliteSession>(), db.Identity));
        services.AddSingleton<IRevocationDeliveryActivity>(activity);
        using var provider = services.BuildServiceProvider();
        var calls = new List<string>();
        var runtime = new SyncRuntimeService(provider.GetRequiredService<IServiceScopeFactory>(), db.Identity,
            new EnrollmentRuntimeState(), new FakeSyncDeviceIdentityService(), new DiscoveredDeviceEndpointRegistry(),
            new FakeDeviceSyncTaskService(), [new FakeControlledHostedService("network", 1, calls)]);
        var supervisor = new RevocationDeliveryRuntimeHostedService(activity, runtime);
        await supervisor.StartAsync();
        try
        {
            await UntilAsync(() => runtime.Snapshot.State == SyncRuntimeState.Running);
            var link = (await db.Db.UserDevices.GetAsync(user.UId, peer!.Id))!;
            await using (var rollback = await db.Db.UnitOfWork.BeginTransactionAsync())
            {
                link.IsSyncOn = false; db.Db.UserDevices.Update(link);
                await db.Db.UnitOfWork.SaveChangesAsync();
                await Task.Delay(150);
                Assert.AreEqual(SyncRuntimeState.Running, runtime.Snapshot.State, "Uncommitted changes must not drive networking.");
                await rollback.RollbackAsync();
            }
            Assert.AreEqual(1, calls.Count(call => call == "start:network"));
            link = (await db.Db.UserDevices.GetAsync(user.UId, peer.Id))!;
            link.IsSyncOn = false; db.Db.UserDevices.Update(link); await db.Db.UnitOfWork.SaveChangesAsync();
            await UntilAsync(() => runtime.Snapshot.State == SyncRuntimeState.Disabled);
            link.IsSyncOn = true; db.Db.UserDevices.Update(link); await db.Db.UnitOfWork.SaveChangesAsync();
            await UntilAsync(() => runtime.Snapshot.State == SyncRuntimeState.Running);
            peer = (await db.Db.Devices.GetByIdAsync(peer.Id))!;
            peer.IsBlocked = true; db.Db.Devices.Update(peer); await db.Db.UnitOfWork.SaveChangesAsync();
            await UntilAsync(() => runtime.Snapshot.State == SyncRuntimeState.Disabled);
            peer.IsBlocked = false; db.Db.Devices.Update(peer); await db.Db.UnitOfWork.SaveChangesAsync();
            await UntilAsync(() => runtime.Snapshot.State == SyncRuntimeState.Running);
            var member = (await db.Db.UserMembershipAuthorizations.ListActiveForDeviceAsync(user.UId, peer.Id)).Single();
            member.IsActive = false; member.EndedMembershipEpoch = 3;
            db.Db.UserMembershipAuthorizations.Update(member); await db.Db.UnitOfWork.SaveChangesAsync();
            await UntilAsync(() => runtime.Snapshot.State == SyncRuntimeState.Disabled);
            Assert.AreEqual(3, calls.Count(call => call == "start:network"));
        }
        finally { await supervisor.StopAsync(); await runtime.StopAsync(); }
    }

    [TestMethod]
    public void EveryAuthorityMutationQueuesReconciliation_ButHeartbeatAndDerivedRuntimeFlagsDoNot()
    {
        object[] mutations = [new User(), new LocalUserDevice(), new UserDevice(), new Device(),
            new UserMembershipAuthorization(), new UserControlState(), new UserControlOperation(),
            new UserOriginRemovalCutoff(), new DeletedUserBarrier(), new LocalDeviceIdentity()];
        foreach (var row in mutations)
        {
            var signal = new RecordingActivity();
            var changes = new BackendChangeSet();
            changes.AddEntityMutation(row, addedOrDeleted: true, _ => true);
            changes.Publish(null, signal);
            Assert.AreEqual(1, signal.Refreshes, row.GetType().Name);
        }
        var idle = new RecordingActivity();
        var heartbeat = new BackendChangeSet();
        heartbeat.AddEntityMutation(new Device(), false, name => name == nameof(Device.LastSeen));
        heartbeat.AddEntityMutation(new LocalDeviceIdentity(), false, name => name == nameof(LocalDeviceIdentity.IsSyncOn));
        heartbeat.Publish(null, idle);
        Assert.AreEqual(0, idle.Refreshes);
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }
    private sealed class RecordingActivity : IRevocationDeliveryActivity
    {
        public int Refreshes { get; private set; }
        public bool HasActiveWork => false;
        public IDisposable Enter() => throw new NotSupportedException();
        public void RequestRefresh() => Refreshes++;
        public IAsyncEnumerable<bool> ReadRefreshesAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
