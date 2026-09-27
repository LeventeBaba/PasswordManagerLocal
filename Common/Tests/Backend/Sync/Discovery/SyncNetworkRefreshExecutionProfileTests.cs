using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Services.Hosted;
using PasswordManagerLocal.Common.Backend.State;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Sync.Tcp;
using PasswordManagerLocal.Common.Tests.Fakes;
using System.Net;

namespace PasswordManagerLocal.Common.Tests.Backend.Sync.Discovery;

[TestClass]
public sealed class SyncNetworkRefreshExecutionProfileTests
{
    [TestMethod]
    public async Task BackgroundPollingWait_InteractiveProfileChangeWakesImmediately()
    {
        var profiles = CreateProfiles(TimeSpan.FromSeconds(30), isInteractive: false);
        var networkAddresses = new FakeLocalNetworkAddressService();
        using var service = CreateService(profiles, networkAddresses);
        await service.StartAsync(CancellationToken.None);
        networkAddresses.MulticastInterfaceAddresses = [IPAddress.Parse("192.168.1.11")];

        profiles.SetProfile(CreateProfile(TimeSpan.FromMilliseconds(20)), isInteractive: true);

        await WaitForAsync(() => service.HasPendingRefresh);
        await service.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task OperatingSystemNetworkEvent_SchedulesRefreshWithoutWaitingForPollingInterval()
    {
        var profiles = CreateProfiles(TimeSpan.FromSeconds(30), isInteractive: false);
        using var service = CreateService(profiles, new FakeLocalNetworkAddressService());
        await service.StartAsync(CancellationToken.None);

        service.NotifyNetworkChange();

        Assert.IsTrue(service.HasPendingRefresh);
        await service.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task ProfileChangeAndNetworkEventRace_LeavesOneSafePendingRefreshPath()
    {
        var profiles = CreateProfiles(TimeSpan.FromSeconds(30), isInteractive: false);
        var networkAddresses = new FakeLocalNetworkAddressService();
        using var service = CreateService(profiles, networkAddresses);
        await service.StartAsync(CancellationToken.None);
        networkAddresses.MulticastInterfaceAddresses = [IPAddress.Parse("192.168.1.12")];

        await Task.WhenAll(
            Task.Run(() => profiles.SetProfile(CreateProfile(TimeSpan.FromMilliseconds(20)), isInteractive: true)),
            Task.Run(service.NotifyNetworkChange));

        await WaitForAsync(() => service.HasPendingRefresh);
        await service.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task Stop_CancelsPollingWaitAndProfileChangeDoesNotRestartIt()
    {
        var profiles = CreateProfiles(TimeSpan.FromSeconds(30), isInteractive: false);
        using var service = CreateService(profiles, new FakeLocalNetworkAddressService());
        await service.StartAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);
        profiles.SetProfile(CreateProfile(TimeSpan.FromMilliseconds(20)), isInteractive: true);
        service.NotifyNetworkChange();
        await Task.Delay(100);

        Assert.IsFalse(service.HasPendingRefresh);
    }

    private static SyncNetworkRefreshHostedService CreateService(
        FakeBackendExecutionProfileProvider profiles,
        FakeLocalNetworkAddressService networkAddresses)
    {
        var identity = new FakeDeviceIdentityService { IsSyncOn = true };
        var enrollmentState = new EnrollmentRuntimeState();
        var registry = new DiscoveredDeviceEndpointRegistry();
        var syncTasks = new FakeDeviceSyncTaskService();
        var transport = new FakeLocalDiscoveryTransport();
        var discovery = new LocalDiscoveryHostedService(
            identity,
            new FakeSyncDeviceIdentityService(),
            registry,
            syncTasks,
            enrollmentState,
            networkAddresses,
            transport,
            new FakeLocalDiscoveryNetworkLease(),
            profiles);
        var root = new ServiceCollection().BuildServiceProvider();
        var tcpServer = new TcpSyncServerHostedService(
            identity,
            new SyncPeerProtocolHandler(root),
            enrollmentState);
        return new SyncNetworkRefreshHostedService(
            identity,
            enrollmentState,
            registry,
            syncTasks,
            networkAddresses,
            tcpServer,
            discovery,
            profiles);
    }

    private static FakeBackendExecutionProfileProvider CreateProfiles(TimeSpan interval, bool isInteractive)
    {
        var provider = new FakeBackendExecutionProfileProvider();
        provider.SetProfile(CreateProfile(interval), isInteractive);
        return provider;
    }

    private static BackendExecutionProfile CreateProfile(TimeSpan interval) =>
        new(interval, interval, TimeSpan.FromSeconds(35));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                Assert.Fail("The expected network refresh trigger did not occur.");

            await Task.Delay(10);
        }
    }
}
