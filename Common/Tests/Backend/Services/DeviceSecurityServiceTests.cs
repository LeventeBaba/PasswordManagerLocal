using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Tests.Fakes;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class DeviceSecurityServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task RecordInvalidIncomingSync_BelowLimit_IncrementsAndPersistsWithoutBlocking()
    {
        var devices = new FakeDeviceRepository();
        var queue = new FakeSyncQueueService();
        var identities = new FakeSyncDeviceIdentityService();
        var unitOfWork = new FakeUnitOfWork();
        var service = new DeviceSecurityService(devices, queue, identities, unitOfWork);
        var device = CreateDevice();
        devices.Seed(device);

        await service.RecordInvalidIncomingSyncAsync(device, "bad payload");

        MSTestAssert.AreEqual(1, device.InvalidSyncAttemptCount);
        MSTestAssert.IsNotNull(device.LastInvalidSyncAttemptAt);
        MSTestAssert.IsFalse(device.IsBlocked);
        MSTestAssert.AreEqual(1, devices.UpdateCalls);
        MSTestAssert.AreEqual(1, unitOfWork.SaveCalls);
        MSTestAssert.HasCount(0, queue.EnqueuedItems);
        MSTestAssert.AreEqual(0, identities.RemoveCalls);
        device.VerifyIntegrity();
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task RecordInvalidIncomingSync_AtLimit_BlocksRemovesAndQueuesDeviceUpdate()
    {
        var devices = new FakeDeviceRepository();
        var queue = new FakeSyncQueueService();
        var identities = new FakeSyncDeviceIdentityService();
        var unitOfWork = new FakeUnitOfWork();
        var service = new DeviceSecurityService(devices, queue, identities, unitOfWork);
        var device = CreateDevice();
        device.InvalidSyncAttemptCount = SyncConstants.MaxInvalidIncomingSyncAttempts - 1;
        devices.Seed(device);
        identities.TryAdd(device);
        var reason = $"  {new string('x', 600)}  ";

        await service.RecordInvalidIncomingSyncAsync(device, reason);

        MSTestAssert.AreEqual(SyncConstants.MaxInvalidIncomingSyncAttempts, device.InvalidSyncAttemptCount);
        MSTestAssert.IsTrue(device.IsBlocked);
        MSTestAssert.IsNotNull(device.BlockedAt);
        MSTestAssert.IsNotNull(device.LastInvalidSyncAttemptAt);
        MSTestAssert.AreEqual(512, device.BlockedReason?.Length ?? 0);
        MSTestAssert.AreEqual(1, identities.RemoveCalls);
        MSTestAssert.HasCount(1, queue.EnqueuedItems);
        MSTestAssert.AreEqual(device.Id, queue.EnqueuedItems[0].ModelId);
        MSTestAssert.AreEqual(SyncModelType.Device, queue.EnqueuedItems[0].ModelType);
        MSTestAssert.AreEqual(SyncChangeType.Updated, queue.EnqueuedItems[0].ChangeType);
        device.VerifyIntegrity();
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task RecordInvalidIncomingSync_EmptyReason_UsesSafeDefaultWhenBlocked()
    {
        var service = new DeviceSecurityService(
            new FakeDeviceRepository(),
            new FakeSyncQueueService(),
            new FakeSyncDeviceIdentityService(),
            new FakeUnitOfWork());
        var device = CreateDevice();
        device.InvalidSyncAttemptCount = SyncConstants.MaxInvalidIncomingSyncAttempts - 1;

        await service.RecordInvalidIncomingSyncAsync(device, "  ");

        MSTestAssert.AreEqual("Invalid incoming sync data.", device.BlockedReason);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task ResetInvalidIncomingSync_ClearsCountersAndPersists()
    {
        var devices = new FakeDeviceRepository();
        var unitOfWork = new FakeUnitOfWork();
        var service = new DeviceSecurityService(
            devices,
            new FakeSyncQueueService(),
            new FakeSyncDeviceIdentityService(),
            unitOfWork);
        var device = CreateDevice();
        device.InvalidSyncAttemptCount = 3;
        device.LastInvalidSyncAttemptAt = DateTimeOffset.UtcNow;
        devices.Seed(device);

        await service.ResetInvalidIncomingSyncAsync(device);

        MSTestAssert.AreEqual(0, device.InvalidSyncAttemptCount);
        MSTestAssert.IsNull(device.LastInvalidSyncAttemptAt);
        MSTestAssert.AreEqual(1, devices.UpdateCalls);
        MSTestAssert.AreEqual(1, unitOfWork.SaveCalls);
        device.VerifyIntegrity();
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task ResetInvalidIncomingSync_CleanDevice_DoesNothing()
    {
        var devices = new FakeDeviceRepository();
        var unitOfWork = new FakeUnitOfWork();
        var service = new DeviceSecurityService(
            devices,
            new FakeSyncQueueService(),
            new FakeSyncDeviceIdentityService(),
            unitOfWork);

        await service.ResetInvalidIncomingSyncAsync(CreateDevice());

        MSTestAssert.AreEqual(0, devices.UpdateCalls);
        MSTestAssert.AreEqual(0, unitOfWork.SaveCalls);
    }

    private static Device CreateDevice()
    {
        var device = new Device
        {
            Id = Guid.NewGuid(),
            PublicKey = Enumerable.Repeat((byte)1, 32).ToArray(),
            SignPublicKey = Enumerable.Repeat((byte)2, 32).ToArray(),
            TlsCertFingerprint = "AABBCC",
            DeviceType = DeviceType.WindowsPc,
            IsTrusted = true
        };
        device.GenerateIntegrityHash();
        return device;
    }
}
