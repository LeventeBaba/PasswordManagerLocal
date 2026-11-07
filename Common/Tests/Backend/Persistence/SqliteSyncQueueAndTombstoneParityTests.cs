using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories.Sqlite;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
[TestCategory("Backend")]
[TestCategory("Integration")]
public sealed class SqliteSyncQueueAndTombstoneParityTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SparseBatch_RefreshesOnlyRequestedPairs(bool sqlite)
    {
        await using var baseline = await SqliteIntegrationTestDatabase.CreateSharedAsync();
        using var session = new SqliteSession(new SqliteConnectionFactory(baseline.ConnectionString));
        ISyncQueueRepository queue = sqlite ? new SqliteSyncQueueRepository(session) : baseline.SyncQueue;
        IUnitOfWork writes = sqlite ? session : baseline.UnitOfWork;

        var first = NewItem();
        var second = NewItem();
        var left = NewDevice("AAAA");
        var right = NewDevice("BBBB");
        baseline.Db.AddRange(first, second, left, right);
        await baseline.UnitOfWork.SaveChangesAsync();
        await queue.EnqueueAsync([Pair(first, left), Pair(first, right), Pair(second, left), Pair(second, right)]);
        await writes.SaveChangesAsync();

        var before = await baseline.Db.SyncQueueItems.AsNoTracking()
            .ToDictionaryAsync(row => (row.SyncItemId, row.DeviceId));
        var unrelated = before[(first.Id, right.Id)];
        Assert.IsTrue(await queue.TryAcknowledgeAsync(unrelated.Id, unrelated.EnqueuedAt));
        await queue.EnqueueAsync([Pair(first, left), Pair(second, right), Pair(first, left)]);
        await writes.SaveChangesAsync();

        var after = await baseline.Db.SyncQueueItems.AsNoTracking()
            .ToDictionaryAsync(row => (row.SyncItemId, row.DeviceId));
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
        await using var baseline = await SqliteIntegrationTestDatabase.CreateSharedAsync();
        var factory = new SqliteConnectionFactory(baseline.ConnectionString);
        using var session = new SqliteSession(factory);
        using var senderSession = new SqliteSession(factory);
        var queue = new SqliteSyncQueueRepository(session);
        var sender = new SqliteSyncQueueRepository(senderSession);
        var devices = new SqliteDeviceRepository(session);
        var items = new SqliteSyncItemRepository(session);
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
        await using var baseline = await SqliteIntegrationTestDatabase.CreateSharedAsync();
        var factory = new SqliteConnectionFactory(baseline.ConnectionString);
        using var session = new SqliteSession(factory);
        using var otherSession = new SqliteSession(factory);
        var first = new SqliteSyncTombstoneRepository(session, capacity: 1);
        var other = new SqliteSyncTombstoneRepository(otherSession, capacity: 1);
        var abandoned = Guid.NewGuid();
        var retained = Guid.NewGuid();
        await first.UpsertAsync(abandoned, SyncModelType.Group, 3);
        Assert.AreEqual(3L, (await first.GetAsync(abandoned, SyncModelType.Group))!.DeletedAtTs);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => first.UpsertAsync(Guid.NewGuid(), SyncModelType.Group, 4));

        await other.UpsertAsync(retained, SyncModelType.Group, 5);
        await otherSession.SaveChangesAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.SaveChangesAsync());
        session.ClearTrackedChanges();
        Assert.IsNull(await first.GetAsync(abandoned, SyncModelType.Group));

        var stale = (await first.GetAsync(retained, SyncModelType.Group))!;
        await other.UpsertAsync(retained, SyncModelType.Group, 8);
        await other.UpsertAsync(retained, SyncModelType.Group, 6);
        Assert.AreEqual(8L, (await other.GetAsync(retained, SyncModelType.Group))!.DeletedAtTs);
        await otherSession.SaveChangesAsync();
        first.Delete(stale);
        var failure = await Assert.ThrowsExactlyAsync<StorageWriteException>(() => session.SaveChangesAsync());
        Assert.AreEqual(StorageWriteException.FailureKind.StaleRow, failure.Kind);
        var persisted = await baseline.Db.SyncTombstones.AsNoTracking().SingleAsync();
        Assert.AreEqual(stale.Id, persisted.Id);
        Assert.AreEqual(8L, persisted.DeletedAtTs);
    }

    [TestMethod]
    public async Task DeviceObservation_UsesTheStagedTargetAndPreservesItsMetadata()
    {
        await using var baseline = await SqliteIntegrationTestDatabase.CreateSharedAsync();
        using var session = new SqliteSession(new SqliteConnectionFactory(baseline.ConnectionString));
        var devices = new SqliteDeviceRepository(session);
        var seeded = NewDevice("AABB");
        await devices.AddAsync(seeded);
        await session.SaveChangesAsync();

        var source = (await devices.GetByIdAsync(seeded.Id))!;
        var target = (await devices.GetByIdWithUserDevicesAsync(seeded.Id))!;
        target.IsBlocked = true;
        devices.Update(target);
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
        new() { ModelId = Guid.NewGuid(), ModelType = SyncModelType.Group, ChangeType = SyncChangeType.Updated };

    private static Device NewDevice(string fingerprint) =>
        new() { TlsCertFingerprint = fingerprint, DeviceType = DeviceType.WindowsPc };

    private static SyncQueueItem Pair(SyncItem item, Device device) =>
        new() { SyncItemId = item.Id, DeviceId = device.Id };
}
