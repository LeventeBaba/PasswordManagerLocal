using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Text.Json;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Backend.Sync.Tombstones;
namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserSnapshotInboxServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task StoreAsync_WithoutActiveKey_DurablyStoresPendingAndDoesNotReplaceCanonicalBlobs()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = await AddCanonicalUserAsync(database);
        var originalGeneral = user.EncryptedGeneralUserDataPayload.ToArray();
        var originalPasswords = user.EncryptedUserPasswordsDataPayload.ToArray();
        var originalDevices = user.EncryptedUserDevicesDataPayload.ToArray();
        var localIdentity = CreateUnsignedIdentity(Guid.NewGuid(), Guid.NewGuid());
        var recoveryScheduler = new FakeUserDataRecoveryScheduler();
        var service = new UserSnapshotInboxService(
            database.Users,
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            localIdentity,
            database.UnitOfWork,
            new UserLifecycleCoordinator(),
            new FakeUserMembershipAuthorizationService(),
            recoveryScheduler: recoveryScheduler);

        using var originKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var originDeviceId = Guid.NewGuid();
        var originInstanceId = Guid.NewGuid();
        var envelope = CreateSignedEnvelope(user, originDeviceId, originInstanceId, 7, originKey, marker: 0x77);
        var relayDeviceId = Guid.NewGuid();

        var receipt = await service.StoreAsync(envelope, relayDeviceId);

        // Read through a fresh DbContext to prove StoreAsync does not report durable receipt
        // until the pending snapshot and revision knowledge have committed.
        var restartOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(database.Db.Database.GetDbConnection())
            .Options;
        await using var restartedDb = new AppDbContext(restartOptions);
        var stored = await restartedDb.UserSyncSnapshots.SingleOrDefaultAsync(snapshot =>
            snapshot.UserId == user.UId &&
            snapshot.OriginDeviceId == originDeviceId &&
            snapshot.OriginInstanceId == originInstanceId &&
            snapshot.UserKeyEpoch == user.KeyEpoch);
        var reloadedUser = await restartedDb.Users.SingleOrDefaultAsync(candidate => candidate.UId == user.UId);

        MSTestAssert.AreEqual(UserSnapshotReceiptState.StoredPending, receipt.State);
        MSTestAssert.IsNotNull(stored);
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.Pending, stored.Status);
        MSTestAssert.AreEqual(7L, stored.OriginRevision);
        MSTestAssert.AreEqual(originDeviceId, stored.OriginDeviceId);
        MSTestAssert.AreEqual(originInstanceId, stored.OriginInstanceId);
        MSTestAssert.AreEqual(relayDeviceId, stored.LastReceivedFromDeviceId);
        CollectionAssert.AreEqual(envelope.SnapshotHash, stored.SnapshotHash);
        MSTestAssert.IsNotNull(reloadedUser);
        CollectionAssert.AreEqual(originalGeneral, reloadedUser.EncryptedGeneralUserDataPayload);
        CollectionAssert.AreEqual(originalPasswords, reloadedUser.EncryptedUserPasswordsDataPayload);
        CollectionAssert.AreEqual(originalDevices, reloadedUser.EncryptedUserDevicesDataPayload);
        MSTestAssert.AreEqual(
            (user.UId, UserDataRecoveryTrigger.HealthyCandidateReceived),
            recoveryScheduler.Calls.Single());
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task StoreAsync_EnforcesMonotonicRevisionIdempotenceAndOneLatestRowPerOrigin()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = await AddCanonicalUserAsync(database);
        var service = new UserSnapshotInboxService(
            database.Users,
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            CreateUnsignedIdentity(Guid.NewGuid(), Guid.NewGuid()),
            database.UnitOfWork,
            new UserLifecycleCoordinator(),
            new FakeUserMembershipAuthorizationService());
        using var originKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var originDeviceId = Guid.NewGuid();
        var originInstanceId = Guid.NewGuid();

        var revisionTwo = CreateSignedEnvelope(user, originDeviceId, originInstanceId, 2, originKey, marker: 0x22);
        var first = await service.StoreAsync(revisionTwo, Guid.NewGuid());
        await database.UnitOfWork.SaveChangesAsync();

        var revisionOne = CreateSignedEnvelope(user, originDeviceId, originInstanceId, 1, originKey, marker: 0x11);
        var obsolete = await service.StoreAsync(revisionOne, Guid.NewGuid());
        var duplicate = await service.StoreAsync(revisionTwo, Guid.NewGuid());

        var revisionThree = CreateSignedEnvelope(user, originDeviceId, originInstanceId, 3, originKey, marker: 0x33);
        var replaced = await service.StoreAsync(revisionThree, Guid.NewGuid());
        await database.UnitOfWork.SaveChangesAsync();

        var rows = await database.Db.UserSyncSnapshots
            .Where(snapshot => snapshot.UserId == user.UId && snapshot.OriginDeviceId == originDeviceId)
            .ToListAsync();
        var knowledge = await database.UserRevisionKnowledge.GetAsync(
            user.UId,
            originDeviceId,
            originInstanceId,
            user.KeyEpoch);

        MSTestAssert.AreEqual(UserSnapshotReceiptState.StoredPending, first.State);
        MSTestAssert.AreEqual(UserSnapshotReceiptState.ObsoleteRevision, obsolete.State);
        MSTestAssert.AreEqual(UserSnapshotReceiptState.AlreadyStored, duplicate.State);
        MSTestAssert.AreEqual(UserSnapshotReceiptState.ReplacedOlderPending, replaced.State);
        MSTestAssert.HasCount(1, rows);
        MSTestAssert.AreEqual(3L, rows[0].OriginRevision);
        CollectionAssert.AreEqual(revisionThree.SnapshotHash, rows[0].SnapshotHash);
        MSTestAssert.IsNotNull(knowledge);
        MSTestAssert.AreEqual(3L, knowledge.HighestStoredRevision);
        MSTestAssert.AreEqual(0L, knowledge.HighestMergedRevision);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task StoreAsync_SameRevisionDifferentHash_QuarantinesOriginAndRejectsLaterOverwrite()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = await AddCanonicalUserAsync(database);
        var service = new UserSnapshotInboxService(
            database.Users,
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            CreateUnsignedIdentity(Guid.NewGuid(), Guid.NewGuid()),
            database.UnitOfWork,
            new UserLifecycleCoordinator(),
            new FakeUserMembershipAuthorizationService());
        using var originKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var originDeviceId = Guid.NewGuid();
        var originInstanceId = Guid.NewGuid();

        var original = CreateSignedEnvelope(user, originDeviceId, originInstanceId, 9, originKey, marker: 0x44);
        await service.StoreAsync(original, Guid.NewGuid());
        await database.UnitOfWork.SaveChangesAsync();

        var fork = CreateSignedEnvelope(user, originDeviceId, originInstanceId, 9, originKey, marker: 0x45);
        var forkReceipt = await service.StoreAsync(fork, Guid.NewGuid());
        await database.UnitOfWork.SaveChangesAsync();

        var later = CreateSignedEnvelope(user, originDeviceId, originInstanceId, 10, originKey, marker: 0x46);
        var laterReceipt = await service.StoreAsync(later, Guid.NewGuid());
        await database.UnitOfWork.SaveChangesAsync();

        var row = await database.UserSyncSnapshots.GetAsync(
            user.UId,
            originDeviceId,
            originInstanceId,
            user.KeyEpoch);

        MSTestAssert.AreEqual(UserSnapshotReceiptState.Quarantined, forkReceipt.State);
        MSTestAssert.AreEqual(UserSnapshotReceiptState.Quarantined, laterReceipt.State);
        MSTestAssert.IsNotNull(row);
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.Quarantined, row.Status);
        MSTestAssert.AreEqual(9L, row.OriginRevision);
        CollectionAssert.AreEqual(original.SnapshotHash, row.SnapshotHash);
        CollectionAssert.AreEqual(fork.SnapshotHash, row.ConflictingSnapshotHash);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task StoreAsync_ForkOfAlreadyMergedRevision_PersistsQuarantineEvidence()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = await AddCanonicalUserAsync(database);
        var service = new UserSnapshotInboxService(
            database.Users,
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            CreateUnsignedIdentity(Guid.NewGuid(), Guid.NewGuid()),
            database.UnitOfWork,
            new UserLifecycleCoordinator(),
            new FakeUserMembershipAuthorizationService());
        using var originKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var originDeviceId = Guid.NewGuid();
        var originInstanceId = Guid.NewGuid();
        var merged = CreateSignedEnvelope(user, originDeviceId, originInstanceId, 5, originKey, marker: 0x51);
        await database.UserRevisionKnowledge.AddAsync(new UserRevisionKnowledge
        {
            UserId = user.UId,
            OriginDeviceId = originDeviceId,
            OriginInstanceId = originInstanceId,
            UserKeyEpoch = user.KeyEpoch,
            HighestStoredRevision = 5,
            HighestStoredSnapshotHash = merged.SnapshotHash.ToArray(),
            HighestMergedRevision = 5
        });
        await database.UnitOfWork.SaveChangesAsync();

        var fork = CreateSignedEnvelope(user, originDeviceId, originInstanceId, 5, originKey, marker: 0x52);
        var receipt = await service.StoreAsync(fork, Guid.NewGuid());

        database.Db.ChangeTracker.Clear();
        var quarantine = await database.UserSyncSnapshots.GetAsync(
            user.UId,
            originDeviceId,
            originInstanceId,
            user.KeyEpoch);

        MSTestAssert.AreEqual(UserSnapshotReceiptState.Quarantined, receipt.State);
        MSTestAssert.IsNotNull(quarantine);
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.Quarantined, quarantine.Status);
        CollectionAssert.AreEqual(fork.SnapshotHash, quarantine.SnapshotHash);
        CollectionAssert.AreEqual(merged.SnapshotHash, quarantine.ConflictingSnapshotHash);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task StoreAsync_ExactSnapshotAlreadyMergedThroughCoverage_RetainsAuthenticatedMergedReceipt()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = await AddCanonicalUserAsync(database);
        var service = new UserSnapshotInboxService(
            database.Users,
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            CreateUnsignedIdentity(Guid.NewGuid(), Guid.NewGuid()),
            database.UnitOfWork,
            new UserLifecycleCoordinator(),
            new FakeUserMembershipAuthorizationService());
        using var originKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var originDeviceId = Guid.NewGuid();
        var originInstanceId = Guid.NewGuid();
        await database.UserRevisionKnowledge.AddAsync(new UserRevisionKnowledge
        {
            UserId = user.UId,
            OriginDeviceId = originDeviceId,
            OriginInstanceId = originInstanceId,
            UserKeyEpoch = user.KeyEpoch,
            HighestStoredRevision = 0,
            HighestStoredSnapshotHash = [],
            HighestMergedRevision = 5
        });
        await database.UnitOfWork.SaveChangesAsync();
        var envelope = CreateSignedEnvelope(user, originDeviceId, originInstanceId, 5, originKey, marker: 0x54);

        var receipt = await service.StoreAsync(envelope, Guid.NewGuid());

        database.Db.ChangeTracker.Clear();
        var row = await database.UserSyncSnapshots.GetAsync(
            user.UId, originDeviceId, originInstanceId, user.KeyEpoch);
        var knowledge = await database.UserRevisionKnowledge.GetAsync(
            user.UId, originDeviceId, originInstanceId, user.KeyEpoch);
        MSTestAssert.AreEqual(UserSnapshotReceiptState.StoredMergedReceipt, receipt.State);
        MSTestAssert.IsNotNull(row);
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.MergedReceipt, row.Status);
        MSTestAssert.AreEqual(5L, row.OriginRevision);
        MSTestAssert.IsNotNull(knowledge);
        MSTestAssert.AreEqual(5L, knowledge.HighestStoredRevision);
        MSTestAssert.AreEqual(5L, knowledge.HighestMergedRevision);
        CollectionAssert.AreEqual(envelope.SnapshotHash, knowledge.HighestStoredSnapshotHash);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task StoreAsync_ExactPendingEnvelopeLaterCoveredIndirectly_PromotesToMergedReceipt()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = await AddCanonicalUserAsync(database);
        var service = new UserSnapshotInboxService(
            database.Users,
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            CreateUnsignedIdentity(Guid.NewGuid(), Guid.NewGuid()),
            database.UnitOfWork,
            new UserLifecycleCoordinator(),
            new FakeUserMembershipAuthorizationService());
        using var originKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var originDeviceId = Guid.NewGuid();
        var originInstanceId = Guid.NewGuid();
        var envelope = CreateSignedEnvelope(user, originDeviceId, originInstanceId, 5, originKey, marker: 0x55);

        var pending = await service.StoreAsync(envelope, Guid.NewGuid());
        var knowledge = await database.UserRevisionKnowledge.GetAsync(
            user.UId, originDeviceId, originInstanceId, user.KeyEpoch)
            ?? throw new AssertFailedException("Revision knowledge was not stored.");
        knowledge.HighestMergedRevision = envelope.OriginRevision;
        database.UserRevisionKnowledge.Update(knowledge);
        await database.UnitOfWork.SaveChangesAsync();

        var promoted = await service.StoreAsync(envelope, Guid.NewGuid());

        database.Db.ChangeTracker.Clear();
        var row = await database.UserSyncSnapshots.GetAsync(
            user.UId, originDeviceId, originInstanceId, user.KeyEpoch);
        MSTestAssert.AreEqual(UserSnapshotReceiptState.StoredPending, pending.State);
        MSTestAssert.AreEqual(UserSnapshotReceiptState.StoredMergedReceipt, promoted.State);
        MSTestAssert.IsNotNull(row);
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.MergedReceipt, row.Status);
        MSTestAssert.IsEmpty(await database.UserSyncSnapshots.ListPendingForKeyEpochAsync(user.UId, user.KeyEpoch));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task StoreAsync_LogicallyMissingContentBelowNonRetainedKnownRevision_StoresPendingWithoutRegressingKnowledge()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = await AddCanonicalUserAsync(database);
        var service = new UserSnapshotInboxService(
            database.Users,
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            CreateUnsignedIdentity(Guid.NewGuid(), Guid.NewGuid()),
            database.UnitOfWork,
            new UserLifecycleCoordinator(),
            new FakeUserMembershipAuthorizationService());
        using var originKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var originDeviceId = Guid.NewGuid();
        var originInstanceId = Guid.NewGuid();
        var newerKnownHash = Enumerable.Repeat((byte)0x12, 32).ToArray();
        await database.UserRevisionKnowledge.AddAsync(new UserRevisionKnowledge
        {
            UserId = user.UId,
            OriginDeviceId = originDeviceId,
            OriginInstanceId = originInstanceId,
            UserKeyEpoch = user.KeyEpoch,
            HighestStoredRevision = 12,
            HighestStoredSnapshotHash = newerKnownHash.ToArray(),
            HighestMergedRevision = 8
        });
        await database.UnitOfWork.SaveChangesAsync();
        var envelope = CreateSignedEnvelope(user, originDeviceId, originInstanceId, 10, originKey, marker: 0x10);

        var receipt = await service.StoreAsync(envelope, Guid.NewGuid());

        database.Db.ChangeTracker.Clear();
        var row = await database.UserSyncSnapshots.GetAsync(
            user.UId, originDeviceId, originInstanceId, user.KeyEpoch);
        var knowledge = await database.UserRevisionKnowledge.GetAsync(
            user.UId, originDeviceId, originInstanceId, user.KeyEpoch);
        MSTestAssert.AreEqual(UserSnapshotReceiptState.StoredPending, receipt.State);
        MSTestAssert.IsNotNull(row);
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.Pending, row.Status);
        MSTestAssert.AreEqual(10L, row.OriginRevision);
        MSTestAssert.IsNotNull(knowledge);
        MSTestAssert.AreEqual(12L, knowledge.HighestStoredRevision);
        MSTestAssert.AreEqual(8L, knowledge.HighestMergedRevision);
        CollectionAssert.AreEqual(newerKnownHash, knowledge.HighestStoredSnapshotHash);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task StoreAsync_AvailableReceiptOlderThanDurableKnownRevision_RetainsWithoutRegressingKnowledge()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = await AddCanonicalUserAsync(database);
        var service = new UserSnapshotInboxService(
            database.Users,
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            CreateUnsignedIdentity(Guid.NewGuid(), Guid.NewGuid()),
            database.UnitOfWork,
            new UserLifecycleCoordinator(),
            new FakeUserMembershipAuthorizationService());
        using var originKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var originDeviceId = Guid.NewGuid();
        var originInstanceId = Guid.NewGuid();
        var newerKnownHash = Enumerable.Repeat((byte)0x20, 32).ToArray();
        await database.UserRevisionKnowledge.AddAsync(new UserRevisionKnowledge
        {
            UserId = user.UId,
            OriginDeviceId = originDeviceId,
            OriginInstanceId = originInstanceId,
            UserKeyEpoch = user.KeyEpoch,
            HighestStoredRevision = 20,
            HighestStoredSnapshotHash = newerKnownHash.ToArray(),
            HighestMergedRevision = 20
        });
        await database.UnitOfWork.SaveChangesAsync();
        var envelope = CreateSignedEnvelope(user, originDeviceId, originInstanceId, 15, originKey, marker: 0x15);

        var first = await service.StoreAsync(envelope, Guid.NewGuid());
        var duplicate = await service.StoreAsync(envelope, Guid.NewGuid());

        database.Db.ChangeTracker.Clear();
        var row = await database.UserSyncSnapshots.GetAsync(
            user.UId, originDeviceId, originInstanceId, user.KeyEpoch);
        var knowledge = await database.UserRevisionKnowledge.GetAsync(
            user.UId, originDeviceId, originInstanceId, user.KeyEpoch);
        MSTestAssert.AreEqual(UserSnapshotReceiptState.StoredMergedReceipt, first.State);
        MSTestAssert.AreEqual(UserSnapshotReceiptState.AlreadyStored, duplicate.State);
        MSTestAssert.IsNotNull(row);
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.MergedReceipt, row.Status);
        MSTestAssert.AreEqual(15L, row.OriginRevision);
        MSTestAssert.IsNotNull(knowledge);
        MSTestAssert.AreEqual(20L, knowledge.HighestStoredRevision);
        MSTestAssert.AreEqual(20L, knowledge.HighestMergedRevision);
        CollectionAssert.AreEqual(newerKnownHash, knowledge.HighestStoredSnapshotHash);
        MSTestAssert.HasCount(1, await database.Db.UserSyncSnapshots.ToListAsync());
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task StoreAsync_ForkOfOlderRetainedReceiptWithNewerDurableKnowledge_QuarantinesNamespace()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = await AddCanonicalUserAsync(database);
        var service = new UserSnapshotInboxService(
            database.Users,
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            CreateUnsignedIdentity(Guid.NewGuid(), Guid.NewGuid()),
            database.UnitOfWork,
            new UserLifecycleCoordinator(),
            new FakeUserMembershipAuthorizationService());
        using var originKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var originDeviceId = Guid.NewGuid();
        var originInstanceId = Guid.NewGuid();
        await database.UserRevisionKnowledge.AddAsync(new UserRevisionKnowledge
        {
            UserId = user.UId,
            OriginDeviceId = originDeviceId,
            OriginInstanceId = originInstanceId,
            UserKeyEpoch = user.KeyEpoch,
            HighestStoredRevision = 20,
            HighestStoredSnapshotHash = Enumerable.Repeat((byte)0x20, 32).ToArray(),
            HighestMergedRevision = 20
        });
        await database.UnitOfWork.SaveChangesAsync();
        var original = CreateSignedEnvelope(user, originDeviceId, originInstanceId, 15, originKey, marker: 0x15);
        var fork = CreateSignedEnvelope(user, originDeviceId, originInstanceId, 15, originKey, marker: 0x16);

        var stored = await service.StoreAsync(original, Guid.NewGuid());
        var conflicting = await service.StoreAsync(fork, Guid.NewGuid());

        database.Db.ChangeTracker.Clear();
        var row = await database.UserSyncSnapshots.GetAsync(
            user.UId, originDeviceId, originInstanceId, user.KeyEpoch);
        MSTestAssert.AreEqual(UserSnapshotReceiptState.StoredMergedReceipt, stored.State);
        MSTestAssert.AreEqual(UserSnapshotReceiptState.Quarantined, conflicting.State);
        MSTestAssert.IsNotNull(row);
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.Quarantined, row.Status);
        CollectionAssert.AreEqual(original.SnapshotHash, row.SnapshotHash);
        CollectionAssert.AreEqual(fork.SnapshotHash, row.ConflictingSnapshotHash);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task StoreAsync_NewerReceiptMustDominateRetainedCoverageBeforeReplacement()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = await AddCanonicalUserAsync(database);
        var service = new UserSnapshotInboxService(
            database.Users,
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            CreateUnsignedIdentity(Guid.NewGuid(), Guid.NewGuid()),
            database.UnitOfWork,
            new UserLifecycleCoordinator(),
            new FakeUserMembershipAuthorizationService());
        using var originKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var originDeviceId = Guid.NewGuid();
        var originInstanceId = Guid.NewGuid();
        var coveredDeviceId = Guid.NewGuid();
        var coveredInstanceId = Guid.NewGuid();
        await database.UserRevisionKnowledge.AddAsync(new UserRevisionKnowledge
        {
            UserId = user.UId,
            OriginDeviceId = originDeviceId,
            OriginInstanceId = originInstanceId,
            UserKeyEpoch = user.KeyEpoch,
            HighestStoredRevision = 0,
            HighestStoredSnapshotHash = [],
            HighestMergedRevision = 20
        });
        await database.UnitOfWork.SaveChangesAsync();
        var retained = CreateSignedEnvelope(
            user,
            originDeviceId,
            originInstanceId,
            15,
            originKey,
            marker: 0x15,
            coverage:
            [
                new UserSnapshotCoverageEntry
                {
                    OriginDeviceId = coveredDeviceId,
                    OriginInstanceId = coveredInstanceId,
                    UserKeyEpoch = user.KeyEpoch,
                    OriginRevision = 10
                }
            ]);
        var nonDominating = CreateSignedEnvelope(
            user,
            originDeviceId,
            originInstanceId,
            20,
            originKey,
            marker: 0x20,
            coverage: []);

        var first = await service.StoreAsync(retained, Guid.NewGuid());
        var second = await service.StoreAsync(nonDominating, Guid.NewGuid());

        database.Db.ChangeTracker.Clear();
        var row = await database.UserSyncSnapshots.GetAsync(
            user.UId, originDeviceId, originInstanceId, user.KeyEpoch);
        MSTestAssert.AreEqual(UserSnapshotReceiptState.StoredMergedReceipt, first.State);
        MSTestAssert.AreEqual(UserSnapshotReceiptState.Quarantined, second.State);
        MSTestAssert.IsNotNull(row);
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.Quarantined, row.Status);
        MSTestAssert.AreEqual(15L, row.OriginRevision);
        CollectionAssert.AreEqual(retained.SnapshotHash, row.SnapshotHash);
        CollectionAssert.AreEqual(nonDominating.SnapshotHash, row.ConflictingSnapshotHash);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task StoreAsync_NewerDominatingReceipt_ReplacesOlderEvidence()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = await AddCanonicalUserAsync(database);
        var service = new UserSnapshotInboxService(
            database.Users,
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            CreateUnsignedIdentity(Guid.NewGuid(), Guid.NewGuid()),
            database.UnitOfWork,
            new UserLifecycleCoordinator(),
            new FakeUserMembershipAuthorizationService());
        using var originKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var originDeviceId = Guid.NewGuid();
        var originInstanceId = Guid.NewGuid();
        var coveredDeviceId = Guid.NewGuid();
        var coveredInstanceId = Guid.NewGuid();
        await database.UserRevisionKnowledge.AddAsync(new UserRevisionKnowledge
        {
            UserId = user.UId,
            OriginDeviceId = originDeviceId,
            OriginInstanceId = originInstanceId,
            UserKeyEpoch = user.KeyEpoch,
            HighestStoredRevision = 0,
            HighestStoredSnapshotHash = [],
            HighestMergedRevision = 20
        });
        await database.UnitOfWork.SaveChangesAsync();
        var olderCoverage = new UserSnapshotCoverageEntry
        {
            OriginDeviceId = coveredDeviceId,
            OriginInstanceId = coveredInstanceId,
            UserKeyEpoch = user.KeyEpoch,
            OriginRevision = 10
        };
        var retained = CreateSignedEnvelope(
            user,
            originDeviceId,
            originInstanceId,
            15,
            originKey,
            marker: 0x15,
            coverage: [olderCoverage]);
        var dominating = CreateSignedEnvelope(
            user,
            originDeviceId,
            originInstanceId,
            20,
            originKey,
            marker: 0x20,
            coverage:
            [
                new UserSnapshotCoverageEntry
                {
                    OriginDeviceId = coveredDeviceId,
                    OriginInstanceId = coveredInstanceId,
                    UserKeyEpoch = user.KeyEpoch,
                    OriginRevision = 12
                }
            ]);

        var first = await service.StoreAsync(retained, Guid.NewGuid());
        var second = await service.StoreAsync(dominating, Guid.NewGuid());

        database.Db.ChangeTracker.Clear();
        var row = await database.UserSyncSnapshots.GetAsync(
            user.UId, originDeviceId, originInstanceId, user.KeyEpoch);
        MSTestAssert.AreEqual(UserSnapshotReceiptState.StoredMergedReceipt, first.State);
        MSTestAssert.AreEqual(UserSnapshotReceiptState.StoredMergedReceipt, second.State);
        MSTestAssert.IsNotNull(row);
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.MergedReceipt, row.Status);
        MSTestAssert.AreEqual(20L, row.OriginRevision);
        CollectionAssert.AreEqual(dominating.SnapshotHash, row.SnapshotHash);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task ReceiptAcquisition_IndirectMergedKnowledgeRequestsRetainsAndUnblocksCausalEvaluation()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = await AddCanonicalUserAsync(database);
        var originalGeneral = user.EncryptedGeneralUserDataPayload.ToArray();
        var originalPasswords = user.EncryptedUserPasswordsDataPayload.ToArray();
        var originalDevices = user.EncryptedUserDevicesDataPayload.ToArray();
        var deletionOriginDeviceId = Guid.NewGuid();
        var deletionOriginInstanceId = Guid.NewGuid();
        var reportingDeviceId = Guid.NewGuid();
        var reportingInstanceId = Guid.NewGuid();
        var reference = new TombstoneCausalReference
        {
            OriginDeviceId = deletionOriginDeviceId,
            OriginInstanceId = deletionOriginInstanceId,
            UserKeyEpoch = user.KeyEpoch,
            MembershipEpoch = user.MembershipEpoch,
            OriginRevision = 10
        };
        using var reportingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var reportingEnvelope = CreateSignedEnvelope(
            user,
            reportingDeviceId,
            reportingInstanceId,
            5,
            reportingKey,
            marker: 0x45,
            coverage:
            [
                new UserSnapshotCoverageEntry
                {
                    OriginDeviceId = deletionOriginDeviceId,
                    OriginInstanceId = deletionOriginInstanceId,
                    UserKeyEpoch = user.KeyEpoch,
                    OriginRevision = reference.OriginRevision
                }
            ]);

        await database.UserRevisionKnowledge.AddAsync(new UserRevisionKnowledge
        {
            UserId = user.UId,
            OriginDeviceId = reportingDeviceId,
            OriginInstanceId = reportingInstanceId,
            UserKeyEpoch = user.KeyEpoch,
            HighestStoredRevision = 0,
            HighestStoredSnapshotHash = [],
            HighestMergedRevision = 5
        });
        await database.UnitOfWork.SaveChangesAsync();

        var localInventory = Inventory(
            user,
            Revision(reportingDeviceId, reportingInstanceId, stored: 0, merged: 5, known: 0, knownHash: []));
        var remoteInventory = Inventory(
            user,
            Revision(
                reportingDeviceId,
                reportingInstanceId,
                stored: 5,
                merged: 5,
                known: 5,
                knownHash: reportingEnvelope.SnapshotHash,
                retainedHash: reportingEnvelope.SnapshotHash));
        var antiEntropy = new UserSnapshotAntiEntropyService(null!, null!, null!, null!, null!, null!, null!, null!, null!);

        var requests = antiEntropy.FindMissingSnapshots(localInventory, remoteInventory.Users);

        MSTestAssert.HasCount(1, requests);
        MSTestAssert.AreEqual(5L, requests[0].OriginRevision);
        CollectionAssert.AreEqual(reportingEnvelope.SnapshotHash, requests[0].ExpectedSnapshotHash.ToByteArray());

        var authorizations = new[]
        {
            Authorization(user, deletionOriginDeviceId, deletionOriginInstanceId),
            Authorization(user, reportingDeviceId, reportingInstanceId)
        };
        var knowledge = new Dictionary<(Guid DeviceId, Guid OriginInstanceId, long KeyEpoch), UserRevisionKnowledge>
        {
            [(deletionOriginDeviceId, deletionOriginInstanceId, user.KeyEpoch)] = new UserRevisionKnowledge
            {
                UserId = user.UId,
                OriginDeviceId = deletionOriginDeviceId,
                OriginInstanceId = deletionOriginInstanceId,
                UserKeyEpoch = user.KeyEpoch,
                HighestStoredRevision = reference.OriginRevision,
                HighestStoredSnapshotHash = Enumerable.Repeat((byte)0x10, 32).ToArray(),
                HighestMergedRevision = reference.OriginRevision
            }
        };
        var descriptor = new TombstoneDescriptor(
            TombstoneItemType.Password,
            Guid.NewGuid(),
            new SyncVersionStamp
            {
                PhysicalTimeUnixMilliseconds = 1,
                LogicalCounter = 1,
                OriginDeviceId = deletionOriginDeviceId,
                OriginInstanceId = deletionOriginInstanceId
            },
            reference,
            () => UserDataBlobKind.Passwords);
        var receipts = new Dictionary<(Guid DeviceId, Guid OriginInstanceId), List<UserSnapshotEnvelope>>
        {
            [(deletionOriginDeviceId, deletionOriginInstanceId)] =
            [
                new UserSnapshotEnvelope
                {
                    UserId = user.UId,
                    OriginDeviceId = deletionOriginDeviceId,
                    OriginInstanceId = deletionOriginInstanceId,
                    OriginRevision = reference.OriginRevision,
                    UserKeyEpoch = user.KeyEpoch,
                    MembershipEpoch = user.MembershipEpoch
                }
            ]
        };
        var before = TombstoneGarbageCollectionRules.Evaluate(
            user.UId,
            descriptor,
            authorizations,
            new Dictionary<Guid, UserOriginRemovalCutoff[]>(),
            knowledge,
            receipts);
        MSTestAssert.AreEqual(TombstoneGarbageCollectionReason.MissingMergedReceipt, before.Reason);
        MSTestAssert.AreEqual(reportingDeviceId, before.BlockingDeviceId);

        var recoveryScheduler = new FakeUserDataRecoveryScheduler();
        var inbox = new UserSnapshotInboxService(
            database.Users,
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            CreateUnsignedIdentity(Guid.NewGuid(), Guid.NewGuid()),
            database.UnitOfWork,
            new UserLifecycleCoordinator(),
            new FakeUserMembershipAuthorizationService(),
            recoveryScheduler: recoveryScheduler);
        var receipt = await inbox.StoreAsync(reportingEnvelope, Guid.NewGuid());

        database.Db.ChangeTracker.Clear();
        var retained = await database.UserSyncSnapshots.GetAsync(
            user.UId, reportingDeviceId, reportingInstanceId, user.KeyEpoch);
        MSTestAssert.AreEqual(UserSnapshotReceiptState.StoredMergedReceipt, receipt.State);
        MSTestAssert.AreEqual(
            (user.UId, UserDataRecoveryTrigger.HealthyCandidateReceived),
            recoveryScheduler.Calls.Single());
        MSTestAssert.IsNotNull(retained);
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.MergedReceipt, retained.Status);
        MSTestAssert.IsEmpty(await database.UserSyncSnapshots.ListPendingForKeyEpochAsync(user.UId, user.KeyEpoch));
        MSTestAssert.IsEmpty(await database.Db.UserSyncStates.ToListAsync());

        var unchangedUser = await database.Users.GetByIdAsNoTrackingAsync(user.UId);
        MSTestAssert.IsNotNull(unchangedUser);
        CollectionAssert.AreEqual(originalGeneral, unchangedUser.EncryptedGeneralUserDataPayload);
        CollectionAssert.AreEqual(originalPasswords, unchangedUser.EncryptedUserPasswordsDataPayload);
        CollectionAssert.AreEqual(originalDevices, unchangedUser.EncryptedUserDevicesDataPayload);

        var persistedEnvelope = JsonSerializer.Deserialize(
            retained.EnvelopePayload,
            BackendJsonSerializerContext.Default.UserSnapshotEnvelope)
            ?? throw new AssertFailedException("The retained receipt envelope could not be deserialized.");
        receipts[(reportingDeviceId, reportingInstanceId)] = [persistedEnvelope];
        var after = TombstoneGarbageCollectionRules.Evaluate(
            user.UId,
            descriptor,
            authorizations,
            new Dictionary<Guid, UserOriginRemovalCutoff[]>(),
            knowledge,
            receipts);

        MSTestAssert.AreEqual(TombstoneGarbageCollectionReason.Stable, after.Reason);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task StoreAsync_RelayedSnapshot_RetainsOriginalOriginAndOnlyUpdatesLastRelayDiagnostic()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = await AddCanonicalUserAsync(database);
        var service = new UserSnapshotInboxService(
            database.Users,
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            CreateUnsignedIdentity(Guid.NewGuid(), Guid.NewGuid()),
            database.UnitOfWork,
            new UserLifecycleCoordinator(),
            new FakeUserMembershipAuthorizationService());
        using var originKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var originDeviceId = Guid.NewGuid();
        var originInstanceId = Guid.NewGuid();
        var relayB = Guid.NewGuid();
        var relayC = Guid.NewGuid();
        var envelope = CreateSignedEnvelope(user, originDeviceId, originInstanceId, 15, originKey, marker: 0x55);

        await service.StoreAsync(envelope, relayB);
        await database.UnitOfWork.SaveChangesAsync();
        var secondReceipt = await service.StoreAsync(envelope, relayC);
        await database.UnitOfWork.SaveChangesAsync();

        var row = await database.UserSyncSnapshots.GetAsync(
            user.UId,
            originDeviceId,
            originInstanceId,
            user.KeyEpoch);

        MSTestAssert.AreEqual(UserSnapshotReceiptState.AlreadyStored, secondReceipt.State);
        MSTestAssert.IsNotNull(row);
        MSTestAssert.AreEqual(originDeviceId, row.OriginDeviceId);
        MSTestAssert.AreEqual(originInstanceId, row.OriginInstanceId);
        MSTestAssert.AreEqual(15L, row.OriginRevision);
        MSTestAssert.AreEqual(relayC, row.LastReceivedFromDeviceId);
        MSTestAssert.HasCount(1, await database.Db.UserSyncSnapshots.ToListAsync());
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task StoreAsync_PeerClaimsCurrentLocalOriginIdentity_RejectsWithoutCreatingRow()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = await AddCanonicalUserAsync(database);
        using var localKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var localDeviceId = Guid.NewGuid();
        var localInstanceId = Guid.NewGuid();
        var localIdentity = CreateSigningIdentity(localDeviceId, localInstanceId, localKey);
        var service = new UserSnapshotInboxService(
            database.Users,
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            localIdentity,
            database.UnitOfWork,
            new UserLifecycleCoordinator(),
            new FakeUserMembershipAuthorizationService());
        var envelope = CreateSignedEnvelope(user, localDeviceId, localInstanceId, 2, localKey, marker: 0x66);

        var receipt = await service.StoreAsync(envelope, Guid.NewGuid());
        await database.UnitOfWork.SaveChangesAsync();

        MSTestAssert.AreEqual(UserSnapshotReceiptState.Quarantined, receipt.State);
        MSTestAssert.HasCount(0, await database.Db.UserSyncSnapshots.ToListAsync());
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task StoreAsync_HigherRevisionAfterOrdinaryCorruption_BecomesRecoveryCandidate()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = await AddCanonicalUserAsync(database);
        var service = new UserSnapshotInboxService(
            database.Users,
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            CreateUnsignedIdentity(Guid.NewGuid(), Guid.NewGuid()),
            database.UnitOfWork,
            new UserLifecycleCoordinator(),
            new FakeUserMembershipAuthorizationService());
        using var originKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var originDeviceId = Guid.NewGuid();
        var originInstanceId = Guid.NewGuid();

        var revisionFive = CreateSignedEnvelope(user, originDeviceId, originInstanceId, 5, originKey, marker: 0x55);
        await service.StoreAsync(revisionFive, Guid.NewGuid());
        var retained = await database.UserSyncSnapshots.GetAsync(user.UId, originDeviceId, originInstanceId, user.KeyEpoch);
        MSTestAssert.IsNotNull(retained);
        retained.Status = UserSyncSnapshotStatus.IsolatedCorrupt;
        retained.QuarantineReason = "root-integrity-failed";
        database.UserSyncSnapshots.Update(retained);
        var knowledge = await database.UserRevisionKnowledge.GetAsync(
            user.UId, originDeviceId, originInstanceId, user.KeyEpoch);
        MSTestAssert.IsNotNull(knowledge);
        knowledge.HighestMergedRevision = 10;
        database.UserRevisionKnowledge.Update(knowledge);
        await database.UnitOfWork.SaveChangesAsync();

        var revisionSix = CreateSignedEnvelope(user, originDeviceId, originInstanceId, 6, originKey, marker: 0x66);
        var receipt = await service.StoreAsync(revisionSix, Guid.NewGuid());

        database.Db.ChangeTracker.Clear();
        var healedCandidate = await database.UserSyncSnapshots.GetAsync(user.UId, originDeviceId, originInstanceId, user.KeyEpoch);
        MSTestAssert.AreEqual(UserSnapshotReceiptState.ReplacedOlderPending, receipt.State);
        MSTestAssert.IsNotNull(healedCandidate);
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.RecoveryCandidate, healedCandidate.Status);
        MSTestAssert.AreEqual(6L, healedCandidate.OriginRevision);
        MSTestAssert.IsNull(healedCandidate.QuarantineReason);
        CollectionAssert.AreEqual(revisionSix.SnapshotHash, healedCandidate.SnapshotHash);
    }

    private static async Task<User> AddCanonicalUserAsync(SqliteIntegrationTestDatabase database)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var user = new User
        {
            UId = Guid.NewGuid(),
            UsernameHash = Enumerable.Repeat((byte)0x01, 32).ToArray(),
            UsernameSalt = Enumerable.Repeat((byte)0x03, 32).ToArray(),
            PasswordSalt = [0x05, 0x06],
            EncryptedPayload = [0x10],
            EncryptedGeneralUserDataPayload = [0x20],
            EncryptedUserPasswordsDataPayload = [0x30],
            EncryptedUserDevicesDataPayload = [0x40],
            KeyEpoch = 1,
            MembershipEpoch = 1,
            GeneralDataVersionPhysicalTimeUnixMilliseconds = 1_000,
            GeneralDataVersionLogicalCounter = 0,
            GeneralDataVersionOriginDeviceId = Guid.Parse("D40D55E9-8AF1-46D0-B260-3461124F220A"),
            GeneralDataVersionOriginInstanceId = Guid.Parse("54A6DFB6-203C-447C-B9CB-D0E92567FC6B"),
            LastModifiedAt = now,
            UserDataLastModifiedAt = now,
            GeneralUserDataLastModifiedAt = now,
            UserPasswordsDataLastModifiedAt = now,
            UserDevicesDataLastModifiedAt = now
        };
        user.GenerateIntegrityHash();
        await database.Users.AddAsync(user);
        await database.UnitOfWork.SaveChangesAsync();
        return user;
    }

    private static UserSnapshotEnvelope CreateSignedEnvelope(
        User user,
        Guid originDeviceId,
        Guid originInstanceId,
        long revision,
        Key signingKey,
        byte marker,
        IReadOnlyList<UserSnapshotCoverageEntry>? coverage = null)
    {
        var createdAt = DateTimeOffset.UtcNow.AddSeconds(revision);
        var payload = new UserSyncPayload
        {
            UId = user.UId,
            UsernameHash = user.UsernameHash.ToArray(),
            UsernameSalt = user.UsernameSalt.ToArray(),
            GeneralUserDataVersion = new()
            {
                PhysicalTimeUnixMilliseconds = 10_000 + revision,
                LogicalCounter = 0,
                OriginDeviceId = originDeviceId,
                OriginInstanceId = originInstanceId
            },
            PasswordSalt = user.PasswordSalt.ToArray(),
            EncryptedPayload = [marker, 0x01],
            EncryptedGeneralUserDataPayload = [marker, 0x02],
            EncryptedUserPasswordsDataPayload = [marker, 0x03],
            EncryptedUserDevicesDataPayload = [marker, 0x04],
            UserDataLastModifiedAt = createdAt,
            GeneralUserDataLastModifiedAt = createdAt,
            UserPasswordsDataLastModifiedAt = createdAt,
            UserDevicesDataLastModifiedAt = createdAt,
            GroupIds = [],
            DeviceIds = [originDeviceId]
        };
        payload.IntegrityHash = SyncCryptoUtil.CalculateUserHash(payload, createdAt.ToUnixTimeMilliseconds());
        var envelope = new UserSnapshotEnvelope
        {
            UserId = user.UId,
            OriginDeviceId = originDeviceId,
            OriginInstanceId = originInstanceId,
            OriginRevision = revision,
            UserKeyEpoch = user.KeyEpoch,
            MembershipEpoch = user.MembershipEpoch,
            CreatedAtUtc = createdAt,
            User = payload,
            Coverage = coverage?.Select(item => new UserSnapshotCoverageEntry
            {
                OriginDeviceId = item.OriginDeviceId,
                OriginInstanceId = item.OriginInstanceId,
                UserKeyEpoch = item.UserKeyEpoch,
                OriginRevision = item.OriginRevision
            }).ToList() ?? []
        };
        UserSnapshotEnvelopeUtil.FillOriginAuthentication(
            envelope,
            CreateSigningIdentity(originDeviceId, originInstanceId, signingKey));
        return envelope;
    }

    private static UserMembershipAuthorization Authorization(
        User user,
        Guid deviceId,
        Guid originInstanceId) =>
        new()
        {
            AuthorizationId = Guid.NewGuid(),
            UserId = user.UId,
            DeviceId = deviceId,
            OriginInstanceId = originInstanceId,
            StartedMembershipEpoch = 1,
            MinimumKeyEpoch = 1,
            IsActive = true
        };

    private static UserSnapshotInventoryExchangeRequest Inventory(
        User user,
        params UserSnapshotRevisionInventory[] revisions)
    {
        var item = new UserSnapshotUserInventory
        {
            UserId = user.UId.ToString("N"),
            UserKeyEpoch = user.KeyEpoch,
            MembershipEpoch = user.MembershipEpoch
        };
        item.Revisions.AddRange(revisions);
        var result = new UserSnapshotInventoryExchangeRequest();
        result.Users.Add(item);
        return result;
    }

    private static UserSnapshotRevisionInventory Revision(
        Guid originDeviceId,
        Guid originInstanceId,
        long stored,
        long merged,
        long known,
        byte[] knownHash,
        byte[]? retainedHash = null) =>
        new()
        {
            OriginDeviceId = originDeviceId.ToString("N"),
            OriginInstanceId = originInstanceId.ToString("N"),
            UserKeyEpoch = 1,
            HighestStoredRevision = stored,
            HighestStoredSnapshotHash = Google.Protobuf.ByteString.CopyFrom(retainedHash ?? []),
            HighestMergedRevision = merged,
            KnownSnapshotRevision = known,
            KnownSnapshotHash = Google.Protobuf.ByteString.CopyFrom(knownHash),
            RetainedMembershipEpoch = stored > 0 ? 1 : 0
        };

    private static FakeDeviceIdentityService CreateSigningIdentity(Guid deviceId, Guid instanceId, Key key) =>
        new()
        {
            LocalDeviceId = deviceId,
            OriginInstanceId = instanceId,
            SignPublicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            SignHandler = data => SignatureAlgorithm.Ed25519.Sign(key, data)
        };

    private static FakeDeviceIdentityService CreateUnsignedIdentity(Guid deviceId, Guid instanceId) =>
        new()
        {
            LocalDeviceId = deviceId,
            OriginInstanceId = instanceId
        };
}
