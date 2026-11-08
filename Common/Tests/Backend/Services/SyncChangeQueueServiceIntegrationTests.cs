using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Repositories.Sqlite;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;
namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class SyncChangeQueueServiceIntegrationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task IncomingPropagationRefreshesOnlyQueueGenerationsThatContainNewWork(bool newerLocalWork)
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var seeded = await SeedUserRoutesAsync(database, enabledRemoteCount: 2, disabledRemoteCount: 0);
        var service = CreateService(database, seeded.Identity, new FakeSyncAuthorizationService());
        await service.Queue.EnqueueAsync(new SyncItem
        {
            ModelId = seeded.User.UId, ModelType = SyncModelType.User, ChangeType = SyncChangeType.Updated
        });
        var pendingItem = (await database.SyncItems.ListAllAsync()).Single();
        var source = seeded.EnabledRemotes[0];
        var thirdDevice = seeded.EnabledRemotes[1];
        // The sender has already read the row in a different SQLite session, before incoming
        // propagation runs. These operations are deliberately sequenced, without sleeps.
        using var senderSession = database.OpenSession();
        var senderQueue = new SqliteSyncQueueRepository(senderSession);
        var inFlight = await senderQueue.GetNextPendingForDeviceAsync(source.Id);
        MSTestAssert.IsNotNull(inFlight);
        var incomingTimestamp = pendingItem.ChangedAtTs + (newerLocalWork ? -1 : 1);
        await service.Queue.EnqueuePropagationAsync(new SyncItem
        {
            ModelId = seeded.User.UId, ModelType = SyncModelType.User, ChangeType = SyncChangeType.Updated
        }, source.Id, incomingTimestamp);
        var rows = (await database.ReadQueueRowsAsync()).Where(row => row.ProcessedAt == null).ToList();
        MSTestAssert.AreEqual(2, rows.Count);
        MSTestAssert.IsTrue(rows.Any(row => row.DeviceId == thirdDevice.Id), "B must still forward to C.");
        MSTestAssert.IsTrue(rows.Any(row => row.Id == inFlight.Id),
            "An exclusion must not delete a row already owned by an outgoing sender.");
        // This is the generation-aware acknowledgement used by DeviceSyncTaskService.
        // Newer local work refreshes the source row too, so an older in-flight payload
        // cannot delete it. A newer incoming value excludes the source, which leaves
        // that row's generation unchanged and permits its original acknowledgement.
        var acknowledged = await senderQueue.TryAcknowledgeAsync(inFlight.Id, inFlight.EnqueuedAt);
        MSTestAssert.AreEqual(!newerLocalWork, acknowledged);
        var remaining = (await database.ReadQueueRowsAsync()).Where(row => row.ProcessedAt == null).ToList();
        if (newerLocalWork)
        {
            MSTestAssert.HasCount(2, remaining);
            MSTestAssert.IsTrue(remaining.Any(row => row.DeviceId == source.Id));
            MSTestAssert.IsTrue(remaining.Any(row => row.DeviceId == thirdDevice.Id));
        }
        else
        {
            MSTestAssert.HasCount(1, remaining);
            MSTestAssert.AreEqual(thirdDevice.Id, remaining.Single().DeviceId);
        }
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task ReenqueueThatStartedBeforeAcknowledgement_ReactivatesTheProcessedRow()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var seeded = await SeedUserRoutesAsync(database, enabledRemoteCount: 1, disabledRemoteCount: 0);
        var service = CreateService(database, seeded.Identity, new FakeSyncAuthorizationService());
        await service.Queue.EnqueueAsync(new SyncItem
        {
            ModelId = seeded.User.UId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated
        });

        using var senderSession = database.OpenSession();
        using var writerSession = database.OpenSession();
        var senderQueue = new SqliteSyncQueueRepository(senderSession);
        var writerQueue = new SqliteSyncQueueRepository(writerSession);
        var target = seeded.EnabledRemotes.Single();
        var inFlight = await senderQueue.GetNextPendingForDeviceAsync(target.Id);
        MSTestAssert.IsNotNull(inFlight);

        // The writer observes the old pending row but has not committed its refreshed
        // generation yet. The older sender wins the first commit and marks it processed.
        await writerQueue.EnqueueAsync(inFlight.SyncItemId, target.Id);
        MSTestAssert.IsTrue(await senderQueue.TryAcknowledgeAsync(inFlight.Id, inFlight.EnqueuedAt));

        // Saving the enqueue must explicitly reactivate the processed row at its new generation.
        await writerSession.SaveChangesAsync();
        var pending = (await database.ReadQueueRowsAsync()).Single(row => row.DeviceId == target.Id);
        MSTestAssert.IsNull(pending.ProcessedAt);
        MSTestAssert.IsTrue(pending.EnqueuedAt > inFlight.EnqueuedAt);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EnqueueUserUpdate_QueuesOnlyEnabledRemoteDevice_AndDoesNotDuplicatePendingWork()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var seeded = await SeedUserRoutesAsync(database, enabledRemoteCount: 1, disabledRemoteCount: 1);
        var service = CreateService(database, seeded.Identity, new FakeSyncAuthorizationService());
        var originalModifiedAt = seeded.User.LastModifiedAt;

        await service.Queue.EnqueueAsync(new SyncItem
        {
            ModelId = seeded.User.UId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated
        });
        await service.Queue.EnqueueAsync(new SyncItem
        {
            ModelId = seeded.User.UId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated
        });

        var syncItem = (await database.SyncItems.ListAllAsync()).Single();
        var queueItems = await database.ReadQueueRowsAsync();
        MSTestAssert.HasCount(1, queueItems);
        MSTestAssert.AreEqual(seeded.EnabledRemotes.Single().Id, queueItems[0].DeviceId);
        MSTestAssert.AreEqual(syncItem.Id, queueItems[0].SyncItemId);
        var persisted = (await database.Users.GetByIdAsNoTrackingAsync(seeded.User.UId))!;
        MSTestAssert.IsTrue(persisted.LastModifiedAt > originalModifiedAt);
        persisted.VerifyIntegrity();
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EnqueueDeferred_EnrollmentIntroducesNewDeviceToExistingDeviceOnlyAfterCommit()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
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
            await database.Devices.AddAsync(newDevice);
            await database.UserDevices.AddAsync(new UserDevice
            {
                UserId = seeded.User.UId,
                DeviceId = newDevice.Id,
                IsSyncOn = true,
                IsDeleted = false
            });
            await database.UnitOfWork.SaveChangesAsync();

            await service.Queue.EnqueueDeferredAsync(new SyncItem
            {
                ModelId = seeded.User.UId,
                ModelType = SyncModelType.User,
                ChangeType = SyncChangeType.Updated
            });
            await service.Queue.EnqueueDeferredAsync(new SyncItem
            {
                ModelId = newDevice.Id,
                ModelType = SyncModelType.Device,
                ChangeType = SyncChangeType.Created
            });
            await service.Queue.EnqueueDeferredAsync(new SyncItem
            {
                ModelId = SyncIdentityUtil.BuildUserDeviceModelId(seeded.User.UId, newDevice.Id),
                ModelType = SyncModelType.UserDevice,
                ChangeType = SyncChangeType.Created
            });

            var queuedBeforeCommit = await database.ReadQueueRowsAsync();
            MSTestAssert.HasCount(4, queuedBeforeCommit);
            MSTestAssert.AreEqual(3, queuedBeforeCommit.Count(item => item.DeviceId == existingDevice.Id));
            MSTestAssert.AreEqual(1, queuedBeforeCommit.Count(item => item.DeviceId == newDevice.Id));
            MSTestAssert.AreEqual(0, syncIdentities.AddCalls);
            MSTestAssert.IsEmpty(syncTasks.Starts);

            await transaction.CommitAsync();
        }

        await service.Activation.ActivatePendingAsync();

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
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var seeded = await SeedUserRoutesAsync(database, enabledRemoteCount: 2, disabledRemoteCount: 0);
        var service = CreateService(database, seeded.Identity, new FakeSyncAuthorizationService());
        var originalModifiedAt = seeded.User.LastModifiedAt;
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
        var source = seeded.EnabledRemotes[0];
        var target = seeded.EnabledRemotes[1];

        await service.Queue.EnqueuePropagationAsync(new SyncItem
        {
            ModelId = seeded.User.UId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated
        }, source.Id, timestamp);

        var syncItem = (await database.SyncItems.ListAllAsync()).Single();
        var queueItems = await database.ReadQueueRowsAsync();
        MSTestAssert.AreEqual(timestamp, syncItem.ChangedAtTs);
        MSTestAssert.HasCount(1, queueItems);
        MSTestAssert.AreEqual(target.Id, queueItems[0].DeviceId);
        var persisted = (await database.Users.GetByIdAsNoTrackingAsync(seeded.User.UId))!;
        MSTestAssert.AreEqual(originalModifiedAt, persisted.LastModifiedAt);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EnqueueDeletedUserDevice_QueuesRemovedDeviceAndOtherActiveDevices()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var seeded = await SeedUserRoutesAsync(database, enabledRemoteCount: 2, disabledRemoteCount: 0);
        var removed = seeded.EnabledRemotes[0];
        var other = seeded.EnabledRemotes[1];
        var link = (await database.UserDevices.GetAsync(seeded.User.UId, removed.Id))!;
        link.IsDeleted = true;
        link.IsSyncOn = false;
        link.DeletedAt = DateTimeOffset.UtcNow;
        database.UserDevices.Update(link);
        await database.UnitOfWork.SaveChangesAsync();
        var service = CreateService(database, seeded.Identity, new FakeSyncAuthorizationService());

        await service.Queue.EnqueueAsync(new SyncItem
        {
            ModelId = SyncIdentityUtil.BuildUserDeviceModelId(seeded.User.UId, removed.Id),
            ModelType = SyncModelType.UserDevice,
            ChangeType = SyncChangeType.Deleted
        });

        var queueItems = await database.ReadQueueRowsAsync();
        MSTestAssert.HasCount(2, queueItems);
        CollectionAssert.AreEquivalent(new[] { removed.Id, other.Id }, queueItems.Select(item => item.DeviceId).ToArray());
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EnqueuePropagationDeletedUserDevice_DoesNotQueueRemovedDevice()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var seeded = await SeedUserRoutesAsync(database, enabledRemoteCount: 3, disabledRemoteCount: 0);
        var removed = seeded.EnabledRemotes[0];
        var source = seeded.EnabledRemotes[1];
        var target = seeded.EnabledRemotes[2];
        var link = (await database.UserDevices.GetAsync(seeded.User.UId, removed.Id))!;
        link.IsDeleted = true;
        link.IsSyncOn = false;
        link.DeletedAt = DateTimeOffset.UtcNow;
        database.UserDevices.Update(link);
        await database.UnitOfWork.SaveChangesAsync();
        var service = CreateService(database, seeded.Identity, new FakeSyncAuthorizationService());

        await service.Queue.EnqueuePropagationAsync(new SyncItem
        {
            ModelId = SyncIdentityUtil.BuildUserDeviceModelId(seeded.User.UId, removed.Id),
            ModelType = SyncModelType.UserDevice,
            ChangeType = SyncChangeType.Deleted
        }, source.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var queueItem = (await database.ReadQueueRowsAsync()).Single();
        MSTestAssert.AreEqual(target.Id, queueItem.DeviceId);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EnqueueDeletedGroup_CreatesTombstone_AndQueuesEligibleDevice()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var seeded = await SeedUserRoutesAsync(database, enabledRemoteCount: 1, disabledRemoteCount: 0);
        var group = new Group
        {
            Id = Guid.NewGuid(),
            EncryptedPayload = [4, 5, 6],
            LastModifiedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
        };
        group.GenerateIntegrityHash();
        await database.Groups.AddAsync(group);
        database.Groups.SetMember(group, seeded.User, isMember: true);
        await database.UnitOfWork.SaveChangesAsync();
        var service = CreateService(database, seeded.Identity, new FakeSyncAuthorizationService());

        await service.Queue.EnqueueAsync(new SyncItem
        {
            ModelId = group.Id,
            ModelType = SyncModelType.Group,
            ChangeType = SyncChangeType.Deleted
        });

        var tombstone = (await database.Tombstones.GetAsync(group.Id, SyncModelType.Group))!;
        var queueItem = (await database.ReadQueueRowsAsync()).Single();
        var syncItem = (await database.SyncItems.ListAllAsync()).Single();
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
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var seeded = await SeedUserRoutesAsync(database, enabledRemoteCount: 1, disabledRemoteCount: 0);
        var authorization = new FakeSyncAuthorizationService { CanSend = false };
        var service = CreateService(database, seeded.Identity, authorization);

        await service.Queue.EnqueueForDeviceAsync(new SyncItem
        {
            ModelId = seeded.User.UId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated
        }, seeded.EnabledRemotes.Single().Id);

        MSTestAssert.AreEqual(1, (await database.SyncItems.ListAllAsync()).Count);
        MSTestAssert.AreEqual(0, (await database.ReadQueueRowsAsync()).Count);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EnqueuePendingItem_MergesCreatedThenUpdatedAsCreated_AndDeletionWins()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var seeded = await SeedUserRoutesAsync(database, enabledRemoteCount: 1, disabledRemoteCount: 0);
        var group = new Group
        {
            Id = Guid.NewGuid(),
            EncryptedPayload = [7, 8, 9],
            LastModifiedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        group.GenerateIntegrityHash();
        await database.Groups.AddAsync(group);
        database.Groups.SetMember(group, seeded.User, isMember: true);
        await database.UnitOfWork.SaveChangesAsync();
        var service = CreateService(database, seeded.Identity, new FakeSyncAuthorizationService());

        await service.Queue.EnqueueAsync(new SyncItem
        {
            ModelId = group.Id,
            ModelType = SyncModelType.Group,
            ChangeType = SyncChangeType.Created
        });
        await service.Queue.EnqueueAsync(new SyncItem
        {
            ModelId = group.Id,
            ModelType = SyncModelType.Group,
            ChangeType = SyncChangeType.Updated
        });

        var syncItem = (await database.SyncItems.ListAllAsync()).Single();
        MSTestAssert.AreEqual(SyncChangeType.Created, syncItem.ChangeType);

        await service.Queue.EnqueueAsync(new SyncItem
        {
            ModelId = group.Id,
            ModelType = SyncModelType.Group,
            ChangeType = SyncChangeType.Deleted
        });

        var deletedItem = (await database.SyncItems.ListAllAsync()).Single();
        MSTestAssert.AreEqual(syncItem.Id, deletedItem.Id);
        MSTestAssert.AreEqual(SyncChangeType.Deleted, deletedItem.ChangeType);
        MSTestAssert.AreEqual(1, (await database.ReadQueueRowsAsync()).Count);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EnqueueGenericDeletedUser_IsRejectedAndLeavesExistingPendingWorkUntouched()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var seeded = await SeedUserRoutesAsync(database, enabledRemoteCount: 1, disabledRemoteCount: 0);
        var service = CreateService(database, seeded.Identity, new FakeSyncAuthorizationService());

        await service.Queue.EnqueueAsync(new SyncItem
        {
            ModelId = seeded.User.UId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated
        });

        await MSTestAssert.ThrowsExactlyAsync<InvalidOperationException>(() => service.Queue.EnqueueAsync(new SyncItem
        {
            ModelId = seeded.User.UId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Deleted
        }));

        var syncItems = await database.SyncItems.ListAllAsync();
        MSTestAssert.HasCount(1, syncItems);
        MSTestAssert.AreEqual(seeded.User.UId, syncItems[0].ModelId);
        MSTestAssert.AreEqual(SyncModelType.User, syncItems[0].ModelType);
        MSTestAssert.AreEqual(SyncChangeType.Updated, syncItems[0].ChangeType);
        MSTestAssert.AreEqual(1, (await database.ReadQueueRowsAsync()).Count);
    }

    private static (ISyncChangeQueueService Queue, IPendingSyncActivationService Activation) CreateService(
        SqliteRepositoryTestDatabase database,
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
        return (changes, activation);
    }

    private static async Task<SeededUserRoutes> SeedUserRoutesAsync(
        SqliteRepositoryTestDatabase database,
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
        await database.DeviceIdentity.Create(identityModel);
        await database.Users.AddAsync(user);
        await database.LocalUserDevices.AddAsync(new LocalUserDevice
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
            await database.Devices.AddAsync(device);
            await database.UserDevices.AddAsync(new UserDevice
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
            await database.Devices.AddAsync(device);
            await database.UserDevices.AddAsync(new UserDevice
            {
                UserId = user.UId,
                DeviceId = device.Id,
                IsSyncOn = false,
                IsDeleted = false
            });
        }

        await database.UnitOfWork.SaveChangesAsync();
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
