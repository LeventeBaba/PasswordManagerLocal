using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
[TestCategory("Backend")]
[TestCategory("Integration")]
public sealed class SqliteSyncQueueAndTombstoneTests
{
    [TestMethod]
    public async Task Initialize_RemovesLegacyGroupStateAndPreservesPersonalVaultData()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        await using var connection = database.Connections.CreateConnection(enforceForeignKeys: true);
        await connection.OpenAsync();

        using var legacySchema = connection.CreateCommand();
        legacySchema.CommandText = """
            SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name IN ('Groups', 'GroupMembers');
            """;
        Assert.AreEqual(0L, (long)(await legacySchema.ExecuteScalarAsync())!);

        var user = new User { EncryptedUserPasswordsDataPayload = [4, 5, 6] };
        user.GenerateIntegrityHash();
        var device = NewDevice("ABCD");
        var activeItem = NewItem();
        var retiredItem = new SyncItem
        {
            ModelId = Guid.NewGuid(),
            ModelType = (SyncModelType)2,
            ChangeType = SyncChangeType.Updated
        };
        var activeSnapshot = new UserSyncSnapshot
        {
            UserId = user.UId,
            OriginDeviceId = device.Id,
            OriginInstanceId = Guid.NewGuid(),
            Status = UserSyncSnapshotStatus.Pending,
            EnvelopePayload = """{"User":{"GroupIds":[]}}"""u8.ToArray()
        };
        var retiredSnapshot = new UserSyncSnapshot
        {
            UserId = user.UId,
            OriginDeviceId = device.Id,
            OriginInstanceId = Guid.NewGuid(),
            Status = UserSyncSnapshotStatus.Pending,
            EnvelopePayload = """{"User":{"GroupIds":["321f195e-41b8-4b88-909a-0b7ece33ee3e"]}}"""u8.ToArray()
        };

        await database.Users.AddAsync(user);
        await database.Devices.AddAsync(device);
        await database.SyncItems.AddAsync(activeItem);
        await database.SyncItems.AddAsync(retiredItem);
        await database.SyncQueue.EnqueueAsync([Pair(activeItem, device), Pair(retiredItem, device)]);
        await database.Tombstones.UpsertAsync(activeItem.ModelId, activeItem.ModelType, 1);
        await database.Tombstones.UpsertAsync(retiredItem.ModelId, retiredItem.ModelType, 1);
        await database.UserSyncSnapshots.AddAsync(activeSnapshot);
        await database.UserSyncSnapshots.AddAsync(retiredSnapshot);
        await database.UnitOfWork.SaveChangesAsync();

        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                CREATE TABLE "Groups" ("Id" TEXT PRIMARY KEY, "EncryptedPayload" BLOB NOT NULL);
                CREATE TABLE "GroupMembers" (
                    "GroupsId" TEXT REFERENCES "Groups" ("Id"),
                    "UsersUId" TEXT REFERENCES "Users" ("UId"));
                INSERT INTO "Groups" VALUES ($groupId, x'010203');
                INSERT INTO "GroupMembers" VALUES ($groupId, $userId);
                """;
            seed.Parameters.AddWithValue("$groupId", retiredItem.ModelId);
            seed.Parameters.AddWithValue("$userId", user.UId);
            await seed.ExecuteNonQueryAsync();
        }

        await AppDatabaseInitializer.InitializeAsync(connection);
        await AppDatabaseInitializer.InitializeAsync(connection);

        Assert.AreEqual(0L, (long)(await legacySchema.ExecuteScalarAsync())!);
        var persistedUser = await database.Users.GetByIdAsync(user.UId);
        Assert.IsNotNull(persistedUser);
        CollectionAssert.AreEqual(user.EncryptedUserPasswordsDataPayload, persistedUser.EncryptedUserPasswordsDataPayload);
        Assert.IsTrue(persistedUser.IsIntegrityValid());
        Assert.AreEqual(activeItem.Id, (await database.SyncItems.ListAllAsync()).Single().Id);
        Assert.AreEqual(activeItem.Id, (await database.ReadQueueRowsAsync()).Single().SyncItemId);
        Assert.IsNotNull(await database.Tombstones.GetAsync(activeItem.ModelId, activeItem.ModelType));
        Assert.IsNull(await database.Tombstones.GetAsync(retiredItem.ModelId, retiredItem.ModelType));

        var snapshots = (await database.UserSyncSnapshots.ListForUserAsync(user.UId)).ToDictionary(row => row.Id);
        Assert.AreEqual(UserSyncSnapshotStatus.Pending, snapshots[activeSnapshot.Id].Status);
        Assert.AreEqual(UserSyncSnapshotStatus.SupersededBadEvidence, snapshots[retiredSnapshot.Id].Status);
        CollectionAssert.AreEqual(retiredSnapshot.EnvelopePayload, snapshots[retiredSnapshot.Id].EnvelopePayload);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SparseBatch_RefreshesOnlyRequestedPairs(bool separateWriter)
    {
        await using var baseline = await SqliteRepositoryTestDatabase.CreateAsync();
        using var session = baseline.OpenSession();
        ISyncQueueRepository queue = separateWriter ? new SyncQueueRepository(session) : baseline.SyncQueue;
        IUnitOfWork writes = separateWriter ? session : baseline.UnitOfWork;

        var first = NewItem();
        var second = NewItem();
        var left = NewDevice("AAAA");
        var right = NewDevice("BBBB");
        await baseline.SyncItems.AddAsync(first);
        await baseline.SyncItems.AddAsync(second);
        await baseline.Devices.AddAsync(left);
        await baseline.Devices.AddAsync(right);
        await baseline.UnitOfWork.SaveChangesAsync();
        await queue.EnqueueAsync([Pair(first, left), Pair(first, right), Pair(second, left), Pair(second, right)]);
        await writes.SaveChangesAsync();

        var before = (await baseline.ReadQueueRowsAsync()).ToDictionary(row => (row.SyncItemId, row.DeviceId));
        var unrelated = before[(first.Id, right.Id)];
        Assert.IsTrue(await queue.TryAcknowledgeAsync(unrelated.Id, unrelated.EnqueuedAt));
        await queue.EnqueueAsync([Pair(first, left), Pair(second, right), Pair(first, left)]);
        await writes.SaveChangesAsync();

        var after = (await baseline.ReadQueueRowsAsync()).ToDictionary(row => (row.SyncItemId, row.DeviceId));
        Assert.HasCount(4, after);
        Assert.IsTrue(after[(first.Id, left.Id)].EnqueuedAt > before[(first.Id, left.Id)].EnqueuedAt);
        Assert.IsTrue(after[(second.Id, right.Id)].EnqueuedAt > before[(second.Id, right.Id)].EnqueuedAt);
        Assert.AreEqual(unrelated.EnqueuedAt, after[(first.Id, right.Id)].EnqueuedAt);
        Assert.IsNotNull(after[(first.Id, right.Id)].ProcessedAt);
        Assert.AreEqual(before[(second.Id, left.Id)].EnqueuedAt, after[(second.Id, left.Id)].EnqueuedAt);
    }

    [TestMethod]
    public async Task Reenqueue_RefreshesTheExactGenerationAndPreservesBothRowIds()
    {
        await using var baseline = await SqliteRepositoryTestDatabase.CreateAsync();
        using var session = baseline.OpenSession();
        using var senderSession = baseline.OpenSession();
        var queue = new SyncQueueRepository(session);
        var sender = new SyncQueueRepository(senderSession);
        var devices = new DeviceRepository(session);
        var items = new SyncItemRepository(session);
        var device = NewDevice("AABB");
        var first = NewItem();
        var second = NewItem();
        var unreferenced = NewItem();
        await devices.AddAsync(device);
        await items.AddAsync(first);
        await items.AddAsync(second);
        await items.AddAsync(unreferenced);
        await queue.EnqueueAsync([Pair(first, device), Pair(second, device)]);
        await session.SaveChangesAsync();

        Assert.HasCount(0, await sender.GetNextPendingBatchForDeviceAsync(device.Id, 0));
        var inFlight = (await sender.GetNextPendingForDeviceAsync(device.Id))!;
        Assert.AreEqual(first.Id, inFlight.SyncItem!.Id);
        Assert.AreEqual(1, await items.ClearSyncItemsAsync());

        await queue.EnqueueAsync(first.Id, device.Id);
        Assert.IsTrue(await sender.TryAcknowledgeAsync(inFlight.Id, inFlight.EnqueuedAt));
        await session.SaveChangesAsync();
        Assert.IsFalse(await sender.TryAcknowledgeAsync(inFlight.Id, inFlight.EnqueuedAt));

        var pending = await sender.GetNextPendingBatchForDeviceAsync(device.Id, 2);
        Assert.HasCount(2, pending);
        var refreshed = pending[0];
        Assert.AreEqual(inFlight.Id, refreshed.Id);
        Assert.AreEqual(inFlight.QueueId, refreshed.QueueId);
        Assert.IsTrue(refreshed.EnqueuedAt > inFlight.EnqueuedAt);
        Assert.AreEqual(second.Id, pending[1].SyncItemId);
        Assert.IsTrue(await sender.TryAcknowledgeAsync(refreshed.Id, refreshed.EnqueuedAt));
        Assert.AreEqual(0, await items.ClearSyncItemsAsync());

        devices.Delete(device);
        await session.SaveChangesAsync();
        Assert.IsFalse(await queue.HasPendingForDeviceAsync(device.Id));
        Assert.AreEqual(2, await items.ClearSyncItemsAsync());
    }

    [TestMethod]
    public async Task TombstoneCapacity_IsCheckedAtFlushAndOlderEvidenceCannotReplaceNewerEvidence()
    {
        await using var baseline = await SqliteRepositoryTestDatabase.CreateAsync();
        using var session = baseline.OpenSession();
        using var otherSession = baseline.OpenSession();
        var first = new SyncTombstoneRepository(session, capacity: 1);
        var other = new SyncTombstoneRepository(otherSession, capacity: 1);
        var abandoned = Guid.NewGuid();
        var retained = Guid.NewGuid();
        await first.UpsertAsync(abandoned, SyncModelType.Device, 3);
        Assert.AreEqual(3L, (await first.GetAsync(abandoned, SyncModelType.Device))!.DeletedAtTs);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => first.UpsertAsync(Guid.NewGuid(), SyncModelType.Device, 4));

        await other.UpsertAsync(retained, SyncModelType.Device, 5);
        await otherSession.SaveChangesAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.SaveChangesAsync());
        session.ClearTrackedChanges();
        Assert.IsNull(await first.GetAsync(abandoned, SyncModelType.Device));

        var stale = (await first.GetAsync(retained, SyncModelType.Device))!;
        await other.UpsertAsync(retained, SyncModelType.Device, 8);
        await other.UpsertAsync(retained, SyncModelType.Device, 6);
        Assert.AreEqual(8L, (await other.GetAsync(retained, SyncModelType.Device))!.DeletedAtTs);
        await otherSession.SaveChangesAsync();
        first.Delete(stale);
        var failure = await Assert.ThrowsExactlyAsync<StorageWriteException>(() => session.SaveChangesAsync());
        Assert.AreEqual(StorageWriteException.FailureKind.StaleRow, failure.Kind);
        var persisted = (await baseline.Tombstones.GetAsync(retained, SyncModelType.Device))!;
        Assert.AreEqual(stale.Id, persisted.Id);
        Assert.AreEqual(8L, persisted.DeletedAtTs);
    }

    [TestMethod]
    public async Task DeviceObservation_UsesTheStagedTargetAndPreservesItsMetadata()
    {
        await using var baseline = await SqliteRepositoryTestDatabase.CreateAsync();
        using var session = baseline.OpenSession();
        var devices = new DeviceRepository(session);
        var seeded = NewDevice("AABB");
        await devices.AddAsync(seeded);
        await session.SaveChangesAsync();

        var source = (await devices.GetByIdAsync(seeded.Id))!;
        var target = (await devices.GetByIdWithUserDevicesAsync(seeded.Id))!;
        target.IsBlocked = true;
        devices.Update(target);
        Assert.AreSame(target, await devices.GetByIdAsync(seeded.Id));
        Assert.IsFalse((await devices.GetByIdAsNoTrackingAsync(seeded.Id))!.IsBlocked);
        var observation = devices.GetObservationWriteTarget(source)!;
        Assert.AreSame(target, observation);
        observation.LastSeen = observation.LastSeen.AddSeconds(1);
        devices.Update(observation);
        Assert.AreEqual(1, await session.SaveChangesAsync());

        var stored = (await baseline.Devices.GetByIdAsNoTrackingAsync(seeded.Id))!;
        Assert.IsTrue(stored.IsBlocked);
        Assert.AreEqual(observation.LastSeen, stored.LastSeen);
        Assert.IsTrue(stored.IsIntegrityValid());
        devices.Delete(observation);
        Assert.IsNull(devices.GetObservationWriteTarget(source));
    }

    private static SyncItem NewItem() =>
        new() { ModelId = Guid.NewGuid(), ModelType = SyncModelType.Device, ChangeType = SyncChangeType.Updated };

    private static Device NewDevice(string fingerprint) =>
        new() { TlsCertFingerprint = fingerprint, DeviceType = DeviceType.WindowsPc };

    private static SyncQueueItem Pair(SyncItem item, Device device) =>
        new() { SyncItemId = item.Id, DeviceId = device.Id };
}
