using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class PendingSyncActivationServiceTests
{
    [TestMethod]
    public void ActivateDevices_TrustedDeviceWithCachedEndpoint_StartsDelivery()
    {
        var device = CreateRemoteDevice();
        var devices = new FakeDeviceRepository();
        devices.Seed(device);
        var identities = new FakeSyncDeviceIdentityService();
        var endpoints = new DiscoveredDeviceEndpointRegistry();
        var tasks = new FakeDeviceSyncTaskService();
        var identity = new FakeDeviceIdentityService
        {
            IsInitialized = true,
            IsSyncOn = true,
            LocalDeviceId = Guid.NewGuid(),
            FingerprintHex = "LOCAL",
            SignPublicKey = Enumerable.Repeat((byte)9, 32).ToArray()
        };
        endpoints.AddOrUpdate(new DiscoveredDeviceEndpoint
        {
            Host = "192.168.1.20",
            Port = 26688,
            TlsCertFingerprint = device.TlsCertFingerprint
        });
        var service = new PendingSyncActivationService(
            devices,
            identities,
            endpoints,
            tasks,
            identity,
            new LocalDeviceMatcherService(identity));

        service.ActivateDevices([device]);

        MSTestAssert.IsTrue(identities.ContainsId(device.Id));
        MSTestAssert.HasCount(1, tasks.Starts);
        MSTestAssert.AreEqual(device.Id, tasks.Starts[0].Device.Id);
    }

    [TestMethod]
    public void ActivateDevices_LocalSyncDisabled_RemovesDiscoveryState()
    {
        var device = CreateRemoteDevice();
        var devices = new FakeDeviceRepository();
        devices.Seed(device);
        var identities = new FakeSyncDeviceIdentityService();
        identities.TryAdd(device);
        var endpoints = new DiscoveredDeviceEndpointRegistry();
        endpoints.AddOrUpdate(new DiscoveredDeviceEndpoint
        {
            Host = "192.168.1.20",
            Port = 26688,
            TlsCertFingerprint = device.TlsCertFingerprint
        });
        var identity = new FakeDeviceIdentityService
        {
            IsInitialized = true,
            IsSyncOn = false,
            LocalDeviceId = Guid.NewGuid(),
            FingerprintHex = "LOCAL",
            SignPublicKey = Enumerable.Repeat((byte)9, 32).ToArray()
        };
        var service = new PendingSyncActivationService(
            devices,
            identities,
            endpoints,
            new FakeDeviceSyncTaskService(),
            identity,
            new LocalDeviceMatcherService(identity));

        service.ActivateDevices([device]);

        MSTestAssert.IsFalse(identities.ContainsId(device.Id));
        MSTestAssert.IsFalse(endpoints.TryGetByFingerprint(device.TlsCertFingerprint, out _));
    }

    private static Device CreateRemoteDevice()
    {
        var device = new Device
        {
            Id = Guid.NewGuid(),
            PublicKey = Enumerable.Repeat((byte)1, 32).ToArray(),
            SignPublicKey = Enumerable.Repeat((byte)2, 32).ToArray(),
            TlsCertFingerprint = "REMOTE",
            DeviceType = DeviceType.WindowsPc,
            IsTrusted = true,
            IsBlocked = false
        };
        device.GenerateIntegrityHash();
        return device;
    }
}
