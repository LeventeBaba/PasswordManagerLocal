using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Services.Hosted;
using PasswordManagerLocal.Common.Backend.State;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Backend.Sync.Discovery;

[TestClass]
public sealed class LocalDiscoveryNetworkLeaseTests
{
    [TestMethod]
    public async Task Start_AcquiresLeaseBeforeTransportStartup()
    {
        var calls = new List<string>();
        var lease = new FakeLocalDiscoveryNetworkLease(calls);
        var transport = new FakeLocalDiscoveryTransport(calls);
        using var service = CreateService(transport, lease);

        await service.StartAsync();

        CollectionAssert.AreEqual(
            new[] { "lease:acquire", "transport:start" },
            calls);
    }

    [TestMethod]
    public async Task StartAndStop_AcquireAndReleaseExactlyOnce()
    {
        var lease = new FakeLocalDiscoveryNetworkLease();
        var transport = new FakeLocalDiscoveryTransport();
        using var service = CreateService(transport, lease);

        await service.StartAsync();
        await service.StartAsync();
        await service.StopAsync();
        await service.StopAsync();

        Assert.AreEqual(1, lease.AcquireCalls);
        Assert.AreEqual(1, transport.StartCalls);
        Assert.AreEqual(1, transport.StopCalls);
        Assert.AreEqual(1, lease.ReleaseCalls);
        Assert.IsFalse(lease.IsAcquired);
    }

    [TestMethod]
    public async Task TransportStartupFailure_ReleasesLease()
    {
        var lease = new FakeLocalDiscoveryNetworkLease();
        var failure = new InvalidOperationException("transport failed");
        var transport = new FakeLocalDiscoveryTransport { StartFailure = failure };
        using var service = CreateService(transport, lease);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.StartAsync());

        Assert.AreSame(failure, actual);
        Assert.AreEqual(1, lease.AcquireCalls);
        Assert.AreEqual(1, lease.ReleaseCalls);
        Assert.IsFalse(lease.IsAcquired);
    }

    private static LocalDiscoveryHostedService CreateService(
        FakeLocalDiscoveryTransport transport,
        FakeLocalDiscoveryNetworkLease lease) =>
        new(
            new FakeDeviceIdentityService { IsSyncOn = true },
            new FakeSyncDeviceIdentityService(),
            new DiscoveredDeviceEndpointRegistry(),
            new FakeDeviceSyncTaskService(),
            new EnrollmentRuntimeState(),
            new FakeLocalNetworkAddressService(),
            transport,
            lease,
            CreateBackgroundProfileProvider());

    private static FakeBackendExecutionProfileProvider CreateBackgroundProfileProvider()
    {
        var provider = new FakeBackendExecutionProfileProvider();
        provider.SetProfile(
            new BackendExecutionProfile(
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(125)),
            isInteractive: false);
        return provider;
    }

}
