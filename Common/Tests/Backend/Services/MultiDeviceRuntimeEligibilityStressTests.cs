using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.State;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Services.Hosted;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

/// <summary>Real five-vault enrollment, SQL authority and transport; controlled OS networking is simulated.</summary>
[TestClass]
[DoNotParallelize]
public sealed class MultiDeviceRuntimeEligibilityStressTests : MultiDeviceSyncStressFixture
{
    [TestMethod]
    [DataRow(20261115)]
    [DataRow(7719)]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("SyncStress")]
    public async Task PartitionDisableReenableFaultAndRestart_KeepAuthoritativeEligibility(int seed)
    {
        await using var setup = await CreateFiveDeviceSetupAsync(seed);
        // Pairwise enrollment preflight only heals each parent/child pair. Earlier replicas
        // must learn the later signed additions before we partition all five installations.
        var expectedMembers = setup.Devices.Select(device => device.Host.Identity.LocalDeviceId).ToHashSet();
        setup.SetLinks(Chain);
        await HealUntilAsync(setup, seed, Chain, async () =>
        {
            foreach (var device in setup.Devices)
            {
                var members = await device.Host.Endpoints.GetUserDevicesAsync(device.Token);
                if (!members.Select(member => member.DeviceId).ToHashSet().SetEquals(expectedMembers))
                    return false;
            }
            return true;
        }, "complete membership propagation before runtime partitioning");

        var source = setup.Devices[0];
        var network = new SimulatedNetworkService();
        var runtime = CreateRuntime(source, network);
        var activity = source.Host.Services.GetRequiredService<IRevocationDeliveryActivity>();
        var supervisor = new RevocationDeliveryRuntimeHostedService(activity, runtime);
        await supervisor.StartAsync();
        try
        {
            await UntilAsync(() => runtime.Snapshot.State == SyncRuntimeState.Running);
            setup.SetLinks([]);
            using (var scope = source.Host.Services.CreateScope())
                Assert.IsTrue((await scope.ServiceProvider.GetRequiredService<ISyncRuntimeEligibilityService>().EvaluateAsync()).IsEligible,
                    "A complete network partition must not disable discovery eligibility.");
            foreach (var peer in setup.Devices.Skip(1))
                await source.Host.Endpoints.SetUserDeviceSyncOnAsync(source.Token, peer.Host.Identity.LocalDeviceId, false);
            await UntilAsync(() => runtime.Snapshot.State == SyncRuntimeState.Disabled);
            var starts = network.Starts;
            await source.Host.Endpoints.SetUserDeviceSyncOnAsync(source.Token, setup.Devices[1].Host.Identity.LocalDeviceId, true);
            for (var index = 0; index < 100; index++) activity.RequestRefresh();
            await UntilAsync(() => runtime.Snapshot.State == SyncRuntimeState.Running);
            Assert.AreEqual(starts + 1, network.Starts);
            setup.SetLinks([(0, 1)]);
            await KickAsync(source, setup.Devices[1]);
            await KickAsync(setup.Devices[1], source);
            network.IsHealthy = false;
            activity.RequestRefresh();
            await UntilAsync(() => network.Starts == starts + 2 && runtime.Snapshot.State == SyncRuntimeState.Running);
            setup.SetLinks([]);
            await source.Host.Endpoints.SetUserDeviceSyncOnAsync(source.Token, setup.Devices[1].Host.Identity.LocalDeviceId, false);
            await UntilAsync(() => runtime.Snapshot.State == SyncRuntimeState.Disabled);
            Trace(seed, "Partition, duplicate notifications, relationship transitions and controlled-service retry retained eligibility.");
        }
        finally { await supervisor.StopAsync(); await runtime.StopAsync(); }

        await setup.RestartAsync(0);
        source = setup.Devices[0];
        var restartedNetwork = new SimulatedNetworkService();
        var restartedRuntime = CreateRuntime(source, restartedNetwork);
        await restartedRuntime.RefreshSyncEnabledAsync();
        Assert.AreEqual(SyncRuntimeState.Disabled, restartedRuntime.Snapshot.State);
        Assert.AreEqual(0, restartedNetwork.Starts, "Restart must read the disabled peer relationships from SQLite.");
        await restartedRuntime.StopAsync();
    }

    private static SyncRuntimeService CreateRuntime(TestDevice source, SimulatedNetworkService network)
    {
        var services = source.Host.Services;
        // The existing production stress host replaces ISyncRuntimeService with an enrollment-only
        // fake and never opens OS listeners. This test owns exactly one real reconciliation runtime.
        return new SyncRuntimeService(services.GetRequiredService<IServiceScopeFactory>(), source.Host.Identity,
            services.GetRequiredService<IEnrollmentRuntimeState>(), services.GetRequiredService<ISyncDeviceIdentityService>(),
            services.GetRequiredService<IDiscoveredDeviceEndpointRegistry>(), source.Host.SyncTasks, [network]);
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private sealed class SimulatedNetworkService : ISyncControlledHostedService, ISyncRuntimeHealth
    {
        public int StartOrder => 20;
        public int Starts { get; private set; }
        public bool IsHealthy { get; set; }
        public Task StartAsync(CancellationToken ct) { Starts++; IsHealthy = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { IsHealthy = false; return Task.CompletedTask; }
    }
}
