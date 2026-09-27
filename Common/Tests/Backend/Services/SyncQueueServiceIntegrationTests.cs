using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;
namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class SyncQueueServiceIntegrationTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EnqueueUserUpdate_QueuesOnlyEnabledRemoteDevice_AndDoesNotDuplicatePendingWork()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var seeded = await SeedUserRoutesAsync(database, enabledRemoteCount: 1, disabledRemoteCount: 1);
        var service = CreateService(database, seeded.Identity, new FakeSyncAuthorizationService());
        var originalModifiedAt = seeded.User.LastModifiedAt;

        await service.EnqueueAsync(new SyncItem
        {
            ModelId = seeded.User.UId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated
        });
        await service.EnqueueAsync(new SyncItem
        {
            ModelId = seeded.User.UId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated
        });

        var syncItem = await database.Db.SyncItems.SingleAsync();
        var queueItems = await database.Db.SyncQueueItems.ToListAsync();
        MSTestAssert.HasCount(1, queueItems);
        MSTestAssert.AreEqual(seeded.EnabledRemotes.Single().Id, queueItems[0].DeviceId);
        MSTestAssert.AreEqual(syncItem.Id, queueItems[0].SyncItemId);
        MSTestAssert.IsTrue(seeded.User.LastModifiedAt > originalModifiedAt);
        seeded.User.VerifyIntegrity();
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EnqueueDeferred_EnrollmentIntroducesNewDeviceToExistingDeviceOnlyAfterCommit()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var seeded = await SeedUserRoutesAsync(database, enabledRemoteCount: 1, disabledRemoteCount: 0);
        var existingDevice = seeded.EnabledRemotes.Single();
        var newDevice = CreateRemoteDevice("CC00");
        var syncIdentities = new FakeSyncDeviceIdentityService();
        var endpointRegistry = new DiscoveredDeviceEndpointRegistry();
        var syncTasks = new FakeDeviceSyncTaskService();
        var service = CreateService(
            database,
            seeded.Identity,
            new FakeSyncAuthorizationService(),
            syncIdentities,
            endpointRegistry,
            syncTasks);

        endpointRegistry.AddOrUpdate(new DiscoveredDeviceEndpoint
        {
            Host = "127.0.0.1",
            Port = 26688,
            TlsCertFingerprint = existingDevice.TlsCertFingerprint
        });

        await using (var transaction = await database.UnitOfWork.BeginTransactionAsync())
        {
            database.Db.Devices.Add(newDevice);
            database.Db.UserDevices.Add(new UserDevice
            {
                UserId = seeded.User.UId,
                DeviceId = newDevice.Id,
                IsSyncOn = true,
                IsDeleted = false
            });
            await database.Db.SaveChangesAsync();

            await service.EnqueueDeferredAsync(new SyncItem
            {
                ModelId = seeded.User.UId,
                ModelType = SyncModelType.User,
                ChangeType = SyncChangeType.Updated
            });
            await service.EnqueueDeferredAsync(new SyncItem
            {
                ModelId = newDevice.Id,
                ModelType = SyncModelType.Device,
                ChangeType = SyncChangeType.Created
            });
            await service.EnqueueDeferredAsync(new SyncItem
            {
                ModelId = SyncIdentityUtil.BuildUserDeviceModelId(seeded.User.UId, newDevice.Id),
                ModelType = SyncModelType.UserDevice,
                ChangeType = SyncChangeType.Created
            });

            var queuedBeforeCommit = await database.Db.SyncQueueItems.ToListAsync();
            MSTestAssert.HasCount(4, queuedBeforeCommit);
            MSTestAssert.AreEqual(3, queuedBeforeCommit.Count(item => item.DeviceId == existingDevice.Id));
            MSTestAssert.AreEqual(1, queuedBeforeCommit.Count(item => item.DeviceId == newDevice.Id));
            MSTestAssert.AreEqual(0, syncIdentities.AddCalls);
            MSTestAssert.IsEmpty(syncTasks.Starts);

            await transaction.CommitAsync();
        }

        await service.ActivatePendingSyncsAsync();

        MSTestAssert.IsTrue(syncIdentities.ContainsId(existingDevice.Id));
        MSTestAssert.IsTrue(syncIdentities.ContainsId(newDevice.Id));
        MSTestAssert.HasCount(1, syncTasks.Starts);
        MSTestAssert.AreEqual(existingDevice.Id, syncTasks.Starts[0].Device.Id);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EnqueuePropagation_ExcludesSourceDevice_PreservesRemoteTimestamp_AndDoesNotTouchLocalModel()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var seeded = await SeedUserRoutesAsync(database, enabledRemoteCount: 2, disabledRemoteCount: 0);
        var service = CreateService(database, seeded.Identity, new FakeSyncAuthorizationService());
        var originalModifiedAt = seeded.User.LastModifiedAt;
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
        var source = seeded.EnabledRemotes[0];
        var target = seeded.EnabledRemotes[1];

        await service.EnqueuePropagationAsync(new SyncItem
        {
            ModelId = seeded.User.UId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated
        }, source.Id, timestamp);

        var syncItem = await database.Db.SyncItems.SingleAsync();
        var queueItems = await database.Db.SyncQueueItems.ToListAsync();
        MSTestAssert.AreEqual(timestamp, syncItem.ChangedAtTs);
        MSTestAssert.HasCount(1, queueItems);
        MSTestAssert.AreEqual(target.Id, queueItems[0].DeviceId);
        MSTestAssert.AreEqual(originalModifiedAt, seeded.User.LastModifiedAt);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EnqueueDeletedUserDevice_QueuesRemovedDeviceAndOtherActiveDevices()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var seeded = await SeedUserRoutesAsync(database, enabledRemoteCount: 2, disabledRemoteCount: 0);
        var removed = seeded.EnabledRemotes[0];
        var other = seeded.EnabledRemotes[1];
        var link = await database.Db.UserDevices.SingleAsync(ud => ud.UserId == seeded.User.UId && ud.DeviceId == removed.Id);
        link.IsDeleted = true;
        link.IsSyncOn = false;
        link.DeletedAt = DateTimeOffset.UtcNow;
        link.GenerateIntegrityHash();
        await database.Db.SaveChangesAsync();
        var service = CreateService(database, seeded.Identity, new FakeSyncAuthorizationService());

        await service.EnqueueAsync(new SyncItem
        {
            ModelId = SyncIdentityUtil.BuildUserDeviceModelId(seeded.User.UId, removed.Id),
            ModelType = SyncModelType.UserDevice,
            ChangeType = SyncChangeType.Deleted
        });

        var queueItems = await database.Db.SyncQueueItems.OrderBy(item => item.DeviceId).ToListAsync();
        MSTestAssert.HasCount(2, queueItems);
        CollectionAssert.AreEquivalent(new[] { removed.Id, other.Id }, queueItems.Select(item => item.DeviceId).ToArray());
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EnqueuePropagationDeletedUserDevice_DoesNotQueueRemovedDevice()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var seeded = await SeedUserRoutesAsync(database, enabledRemoteCount: 3, disabledRemoteCount: 0);
        var removed = seeded.EnabledRemotes[0];
        var source = seeded.EnabledRemotes[1];
        var target = seeded.EnabledRemotes[2];
        var link = await database.Db.UserDevices.SingleAsync(ud => ud.UserId == seeded.User.UId && ud.DeviceId == removed.Id);
        link.IsDeleted = true;
        link.IsSyncOn = false;
        link.DeletedAt = DateTimeOffset.UtcNow;
        link.GenerateIntegrityHash();
        await database.Db.SaveChangesAsync();
        var service = CreateService(database, seeded.Identity, new FakeSyncAuthorizationService());

        await service.EnqueuePropagationAsync(new SyncItem
        {
            ModelId = SyncIdentityUtil.BuildUserDeviceModelId(seeded.User.UId, removed.Id),
            ModelType = SyncModelType.UserDevice,
            ChangeType = SyncChangeType.Deleted
        }, source.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var queueItem = await database.Db.SyncQueueItems.SingleAsync();
        MSTestAssert.AreEqual(target.Id, queueItem.DeviceId);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EnqueueDeletedGroup_CreatesTombstone_AndQueuesEligibleDevice()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var seeded = await SeedUserRoutesAsync(database, enabledRemoteCount: 1, disabledRemoteCount: 0);
        var group = new Group
        {
            Id = Guid.NewGuid(),
            EncryptedPayload = [4, 5, 6],
            LastModifiedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
        };
        group.Users.Add(seeded.User);
        group.GenerateIntegrityHash();
        database.Db.Groups.Add(group);
        await database.Db.SaveChangesAsync();
        var service = CreateService(database, seeded.Identity, new FakeSyncAuthorizationService());

        await service.EnqueueAsync(new SyncItem
        {
            ModelId = group.Id,
            ModelType = SyncModelType.Group,
            ChangeType = SyncChangeType.Deleted
        });

        var tombstone = await database.Db.SyncTombstones.SingleAsync();
        var queueItem = await database.Db.SyncQueueItems.SingleAsync();
        var syncItem = await database.Db.SyncItems.SingleAsync();
        MSTestAssert.AreEqual(group.Id, tombstone.ModelId);
        MSTestAssert.AreEqual(SyncModelType.Group, tombstone.ModelType);
        MSTestAssert.AreEqual(syncItem.ChangedAtTs, tombstone.DeletedAtTs);
        MSTestAssert.AreEqual(seeded.EnabledRemotes.Single().Id, queueItem.DeviceId);
        MSTestAssert.AreEqual(SyncChangeType.Deleted, syncItem.ChangeType);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EnqueueForDevice_WhenAuthorizationDenies_PersistsSyncItemWithoutQueueEntry()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var seeded = await SeedUserRoutesAsync(database, enabledRemoteCount: 1, disabledRemoteCount: 0);
        var authorization = new FakeSyncAuthorizationService { CanSend = false };
        var service = CreateService(database, seeded.Identity, authorization);

        await service.EnqueueForDeviceAsync(new SyncItem
        {
            ModelId = seeded.User.UId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated
        }, seeded.EnabledRemotes.Single().Id);

        MSTestAssert.AreEqual(1, await database.Db.SyncItems.CountAsync());
        MSTestAssert.AreEqual(0, await database.Db.SyncQueueItems.CountAsync());
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EnqueuePendingItem_MergesCreatedThenUpdatedAsCreated_AndDeletionWins()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var seeded = await SeedUserRoutesAsync(database, enabledRemoteCount: 1, disabledRemoteCount: 0);
        var group = new Group
        {
            Id = Guid.NewGuid(),
            EncryptedPayload = [7, 8, 9],
            LastModifiedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        group.Users.Add(seeded.User);
        group.GenerateIntegrityHash();
        database.Db.Groups.Add(group);
        await database.Db.SaveChangesAsync();
        var service = CreateService(database, seeded.Identity, new FakeSyncAuthorizationService());

        await service.EnqueueAsync(new SyncItem
        {
            ModelId = group.Id,
            ModelType = SyncModelType.Group,
            ChangeType = SyncChangeType.Created
        });
        await service.EnqueueAsync(new SyncItem
        {
            ModelId = group.Id,
            ModelType = SyncModelType.Group,
            ChangeType = SyncChangeType.Updated
        });

        var syncItem = await database.Db.SyncItems.SingleAsync();
        MSTestAssert.AreEqual(SyncChangeType.Created, syncItem.ChangeType);

        await service.EnqueueAsync(new SyncItem
        {
            ModelId = group.Id,
            ModelType = SyncModelType.Group,
            ChangeType = SyncChangeType.Deleted
        });

        MSTestAssert.AreEqual(SyncChangeType.Deleted, syncItem.ChangeType);
        MSTestAssert.AreEqual(1, await database.Db.SyncQueueItems.CountAsync());
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EnqueueGenericDeletedUser_IsRejectedAndLeavesExistingPendingWorkUntouched()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var seeded = await SeedUserRoutesAsync(database, enabledRemoteCount: 1, disabledRemoteCount: 0);
        var service = CreateService(database, seeded.Identity, new FakeSyncAuthorizationService());

        await service.EnqueueAsync(new SyncItem
        {
            ModelId = seeded.User.UId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated
        });

        await MSTestAssert.ThrowsExactlyAsync<InvalidOperationException>(() => service.EnqueueAsync(new SyncItem
        {
            ModelId = seeded.User.UId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Deleted
        }));

        var syncItems = await database.Db.SyncItems.ToListAsync();
        MSTestAssert.HasCount(1, syncItems);
        MSTestAssert.AreEqual(seeded.User.UId, syncItems[0].ModelId);
        MSTestAssert.AreEqual(SyncModelType.User, syncItems[0].ModelType);
        MSTestAssert.AreEqual(SyncChangeType.Updated, syncItems[0].ChangeType);
        MSTestAssert.AreEqual(1, await database.Db.SyncQueueItems.CountAsync());
    }

    private static SyncQueueService CreateService(
        SqliteIntegrationTestDatabase database,
        FakeDeviceIdentityService identity,
        FakeSyncAuthorizationService authorization,
        FakeSyncDeviceIdentityService? syncIdentities = null,
        DiscoveredDeviceEndpointRegistry? endpointRegistry = null,
        FakeDeviceSyncTaskService? syncTasks = null)
    {
        syncIdentities ??= new FakeSyncDeviceIdentityService();
        endpointRegistry ??= new DiscoveredDeviceEndpointRegistry();
        syncTasks ??= new FakeDeviceSyncTaskService();

        var localDevices = new LocalDeviceMatcherService(identity);
        var lifecycle = new SyncItemLifecycleService(
            database.SyncItems,
            database.SyncQueue,
            database.Users,
            database.Groups,
            database.Devices,
            database.UserDevices,
            database.Tombstones,
            localDevices);
        var targets = new SyncTargetResolverService(
            database.Groups,
            database.Devices,
            database.UserDevices,
            database.LocalUserDevices,
            localDevices);
        var activation = new PendingSyncActivationService(
            database.Devices,
            syncIdentities,
            endpointRegistry,
            syncTasks,
            identity,
            localDevices);
        var writer = new SyncQueueWriterService(
            database.SyncQueue,
            lifecycle,
            targets,
            activation,
            database.Devices,
            identity,
            authorization,
            database.UnitOfWork);
        var changes = new SyncChangeQueueService(writer);
        var catchUp = new UserSyncCatchUpService(database.Users, database.Devices, writer);
        return new SyncQueueService(changes, catchUp, activation);
    }

    private static async Task<SeededUserRoutes> SeedUserRoutesAsync(
        SqliteIntegrationTestDatabase database,
        int enabledRemoteCount,
        int disabledRemoteCount)
    {
        var localId = Guid.NewGuid();
        var identityModel = new LocalDeviceIdentity
        {
            Id = localId,
            AgreementPrivateKeyBlob = [1],
            SignPrivateKeyBlob = [2],
            PFXCertificate = [3],
            DeviceType = DeviceType.WindowsPc,
            IsSyncOn = true
        };
        identityModel.GenerateIntegrityHash();
        var user = new User
        {
            UId = Guid.NewGuid(),
            UsernameHash = [1],
            UsernameSalt = [2],
            PasswordSalt = [3],
            EncryptedPayload = [4],
            LastModifiedAt = DateTimeOffset.UtcNow.AddHours(-1)
        };
        user.GenerateIntegrityHash();
        database.Db.LocalDeviceIdentities.Add(identityModel);
        database.Db.Users.Add(user);
        database.Db.LocalUserDevices.Add(new LocalUserDevice
        {
            UserId = user.UId,
            LocalDeviceIdentityId = localId,
            IsSyncOn = true
        });

        var enabledRemotes = new List<Device>();
        for (var i = 0; i < enabledRemoteCount; i++)
        {
            var device = CreateRemoteDevice($"AA{i:D2}");
            enabledRemotes.Add(device);
            database.Db.Devices.Add(device);
            database.Db.UserDevices.Add(new UserDevice
            {
                UserId = user.UId,
                DeviceId = device.Id,
                IsSyncOn = true,
                IsDeleted = false
            });
        }

        var disabledRemotes = new List<Device>();
        for (var i = 0; i < disabledRemoteCount; i++)
        {
            var device = CreateRemoteDevice($"BB{i:D2}");
            disabledRemotes.Add(device);
            database.Db.Devices.Add(device);
            database.Db.UserDevices.Add(new UserDevice
            {
                UserId = user.UId,
                DeviceId = device.Id,
                IsSyncOn = false,
                IsDeleted = false
            });
        }

        await database.Db.SaveChangesAsync();
        var identity = new FakeDeviceIdentityService
        {
            IsInitialized = true,
            IsSyncOn = true,
            LocalDeviceId = localId,
            FingerprintHex = "LOCAL",
            SignPublicKey = Enumerable.Repeat((byte)9, 32).ToArray()
        };
        return new SeededUserRoutes(user, enabledRemotes, disabledRemotes, identity);
    }

    private static Device CreateRemoteDevice(string fingerprint)
    {
        var device = new Device
        {
            Id = Guid.NewGuid(),
            PublicKey = Enumerable.Repeat((byte)1, 32).ToArray(),
            SignPublicKey = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray(),
            TlsCertFingerprint = fingerprint,
            DeviceType = DeviceType.WindowsPc,
            IsTrusted = true,
            IsBlocked = false
        };
        device.GenerateIntegrityHash();
        return device;
    }

}
