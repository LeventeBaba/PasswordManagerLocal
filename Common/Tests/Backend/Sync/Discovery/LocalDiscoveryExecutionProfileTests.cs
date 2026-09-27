using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using PasswordManagerLocal.Common.Backend.Services.Hosted;
using PasswordManagerLocal.Common.Backend.State;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Backend.Sync.Discovery;

[TestClass]
public sealed class LocalDiscoveryExecutionProfileTests
{
    [TestMethod]
    public async Task BackgroundWait_InteractiveProfileChangeWakesImmediately()
    {
        var profiles = CreateProfiles(TimeSpan.FromSeconds(30), isInteractive: false);
        var transport = new FakeLocalDiscoveryTransport();
        using var service = CreateService(transport, profiles);
        await service.StartAsync();
        await WaitForAsync(() => transport.MulticastPayloads.Count >= 1);

        profiles.SetProfile(CreateProfile(TimeSpan.FromMilliseconds(20)), isInteractive: true);

        await WaitForAsync(() => transport.MulticastPayloads.Count >= 2);
    }

    [TestMethod]
    public async Task InteractiveWait_BackgroundProfileChangeWakesImmediately()
    {
        var profiles = CreateProfiles(TimeSpan.FromSeconds(30), isInteractive: true);
        var transport = new FakeLocalDiscoveryTransport();
        using var service = CreateService(transport, profiles);
        await service.StartAsync();
        await WaitForAsync(() => transport.MulticastPayloads.Count >= 1);

        profiles.SetProfile(CreateProfile(TimeSpan.FromMilliseconds(20)), isInteractive: false);

        await WaitForAsync(() => transport.MulticastPayloads.Count >= 2);
    }

    [TestMethod]
    public async Task SameProfileNotificationSuppression_DoesNotCreateBusyLoop()
    {
        var profile = CreateProfile(TimeSpan.FromSeconds(30));
        var profiles = new FakeBackendExecutionProfileProvider();
        profiles.SetProfile(profile, isInteractive: false);
        var transport = new FakeLocalDiscoveryTransport();
        using var service = CreateService(transport, profiles);
        await service.StartAsync();
        await WaitForAsync(() => transport.MulticastPayloads.Count >= 1);

        profiles.SetProfile(profile, isInteractive: false);
        await Task.Delay(150);

        Assert.AreEqual(1, transport.MulticastPayloads.Count);
    }

    [TestMethod]
    public async Task BackgroundOnly_KeepsGeneralDiscoveryRunningAndRejectsEnrollmentActivation()
    {
        var profiles = CreateProfiles(TimeSpan.FromSeconds(30), isInteractive: false);
        var transport = new FakeLocalDiscoveryTransport();
        using var service = CreateService(transport, profiles);

        await service.StartAsync();
        await WaitForAsync(() => transport.MulticastPayloads.Count >= 1);
        var exception = Assert.Throws<DeviceEnrollmentException>(() =>
            service.ActivateEnrollmentSession(
                "ABCDEFG2",
                Enumerable.Repeat((byte)0x41, 16).ToArray(),
                DateTimeOffset.UtcNow.AddMinutes(10)));

        Assert.AreEqual(DeviceEnrollmentErrorCode.InteractiveSessionRequired, exception.ErrorCode);
        Assert.IsTrue(transport.IsStarted);
    }

    [TestMethod]
    public async Task Stop_CancelsCurrentWaitAndDoesNotStartReplacementLoop()
    {
        var profiles = CreateProfiles(TimeSpan.FromSeconds(30), isInteractive: false);
        var transport = new FakeLocalDiscoveryTransport();
        using var service = CreateService(transport, profiles);
        await service.StartAsync();
        await WaitForAsync(() => transport.MulticastPayloads.Count >= 1);

        await service.StopAsync();
        profiles.SetProfile(CreateProfile(TimeSpan.FromMilliseconds(20)), isInteractive: true);
        await Task.Delay(150);

        Assert.AreEqual(1, transport.MulticastPayloads.Count);
    }

    private static LocalDiscoveryHostedService CreateService(
        FakeLocalDiscoveryTransport transport,
        FakeBackendExecutionProfileProvider profiles) =>
        new(
            new FakeDeviceIdentityService
            {
                IsSyncOn = true,
                SignHandler = _ => new byte[SyncConstants.LocalDiscoverySignatureBytes]
            },
            new FakeSyncDeviceIdentityService(),
            new DiscoveredDeviceEndpointRegistry(),
            new FakeDeviceSyncTaskService(),
            new EnrollmentRuntimeState(),
            new FakeLocalNetworkAddressService(),
            transport,
            new FakeLocalDiscoveryNetworkLease(),
            profiles);

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
                Assert.Fail("The expected discovery action did not occur.");

            await Task.Delay(10);
        }
    }
}
