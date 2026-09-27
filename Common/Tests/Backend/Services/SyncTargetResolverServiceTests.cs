using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class SyncTargetResolverServiceTests
{
    [TestMethod]
    public async Task ResolveTargets_UserChange_ReturnsOnlyEnabledRemoteDevices()
    {
        var userId = Guid.NewGuid();
        var localId = Guid.NewGuid();
        var identity = new FakeDeviceIdentityService
        {
            IsInitialized = true,
            IsSyncOn = true,
            LocalDeviceId = localId,
            FingerprintHex = "LOCAL",
            SignPublicKey = Enumerable.Repeat((byte)9, 32).ToArray()
        };
        var local = CreateDevice(localId, "LOCAL", identity.SignPublicKey);
        var enabled = CreateDevice(Guid.NewGuid(), "ENABLED", Enumerable.Repeat((byte)1, 32).ToArray());
        var disabled = CreateDevice(Guid.NewGuid(), "DISABLED", Enumerable.Repeat((byte)2, 32).ToArray());
        var userDevices = new FakeUserDeviceRepository();
        await userDevices.AddAsync(CreateLink(userId, local, isSyncOn: true));
        await userDevices.AddAsync(CreateLink(userId, enabled, isSyncOn: true));
        await userDevices.AddAsync(CreateLink(userId, disabled, isSyncOn: false));
        var localUsers = new FakeLocalUserDeviceRepository();
        await localUsers.AddAsync(new LocalUserDevice
        {
            UserId = userId,
            LocalDeviceIdentityId = localId,
            IsSyncOn = true
        });
        var service = new SyncTargetResolverService(
            new FakeGroupRepository(),
            new FakeDeviceRepository(),
            userDevices,
            localUsers,
            new LocalDeviceMatcherService(identity));

        var targets = await service.ResolveTargetsAsync(new SyncItem
        {
            ModelId = userId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated
        }, touchLocalSyncState: true, excludedDeviceIds: []);

        MSTestAssert.HasCount(1, targets);
        MSTestAssert.AreEqual(enabled.Id, targets[0].Id);
    }

    private static UserDevice CreateLink(Guid userId, Device device, bool isSyncOn)
    {
        var link = new UserDevice
        {
            UserId = userId,
            DeviceId = device.Id,
            Device = device,
            IsSyncOn = isSyncOn,
            IsDeleted = false,
            LastModifiedAt = DateTimeOffset.UtcNow
        };
        link.GenerateIntegrityHash();
        return link;
    }

    private static Device CreateDevice(Guid id, string fingerprint, byte[] signPublicKey)
    {
        var device = new Device
        {
            Id = id,
            PublicKey = Enumerable.Repeat((byte)3, 32).ToArray(),
            SignPublicKey = signPublicKey,
            TlsCertFingerprint = fingerprint,
            DeviceType = DeviceType.WindowsPc,
            IsTrusted = true,
            IsBlocked = false
        };
        device.GenerateIntegrityHash();
        return device;
    }
}
