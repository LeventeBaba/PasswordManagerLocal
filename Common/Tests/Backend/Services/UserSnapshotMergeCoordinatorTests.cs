using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Security.Cryptography;
using System.Text.Json;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserSnapshotMergeCoordinatorTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task TryMergePendingAsync_MultipleOrigins_RetainsMergedReceiptsPublishesCoverageAndQueuesAtomically()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var localSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var firstSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var secondSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var localIdentity = CreateIdentity(Guid.NewGuid(), Guid.NewGuid(), localSigningKey);
        var firstOrigin = (DeviceId: Guid.NewGuid(), InstanceId: Guid.NewGuid());
        var secondOrigin = (DeviceId: Guid.NewGuid(), InstanceId: Guid.NewGuid());
        var user = await AddUserAndMembershipAsync(
            database,
            localIdentity,
            (firstOrigin.DeviceId, firstOrigin.InstanceId, firstSigningKey),
            (secondOrigin.DeviceId, secondOrigin.InstanceId, secondSigningKey));

        await AddPendingAsync(database, CreateEnvelope(user, firstOrigin.DeviceId, firstOrigin.InstanceId, 2, firstSigningKey, 0x21));
        await AddPendingAsync(database, CreateEnvelope(user, secondOrigin.DeviceId, secondOrigin.InstanceId, 5, secondSigningKey, 0x52));
        await database.UnitOfWork.SaveChangesAsync();

        var lifecycle = new UserLifecycleCoordinator();
        var bundleSync = new FakeUserDataBundleSyncService
        {
            Handler = (canonical, snapshots, _, _) =>
            {
                var now = DateTimeOffset.UtcNow.AddMinutes(1);
                canonical.EncryptedGeneralUserDataPayload = [0xEE, 0x01];
                canonical.GeneralUserDataLastModifiedAt = now;
                canonical.UserDataLastModifiedAt = now;
                canonical.LastModifiedAt = now;
                canonical.GenerateIntegrityHash();
                return Task.FromResult(new UserSnapshotMergeBatchResult(
                    CanonicalChanged: true,
                    snapshots.Select(snapshot => new UserSnapshotMergeEntryResult(
                        snapshot.OriginDeviceId,
                        snapshot.OriginInstanceId,
                        snapshot.OriginRevision,
                        Verified: true)).ToArray()));
            }
        };
        var publisher = new UserSnapshotPublisherService(
            database.UserSyncSnapshots,
            database.UserSyncStates,
            database.UserRevisionKnowledge,
            localIdentity,
            database.UnitOfWork,
            lifecycle);
        var queue = new FakeSyncQueueWriterService();
        var activation = new FakeSyncQueueService();
        var coordinator = new UserSnapshotMergeCoordinator(
            database.Users,
            CreateMembershipAuthorizationService(database, localIdentity),
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            bundleSync,
            publisher,
            queue,
            activation,
            database.UnitOfWork,
            lifecycle);

        using var key = EncryptionKey.Create();
        var merged = await coordinator.TryMergePendingAsync(user.UId, key);

        database.Db.ChangeTracker.Clear();
        var rows = await database.Db.UserSyncSnapshots.Where(snapshot => snapshot.UserId == user.UId).ToListAsync();
        var published = rows.Single(snapshot => snapshot.Status == UserSyncSnapshotStatus.LocalPublished);
        var publishedEnvelope = JsonSerializer.Deserialize(
            published.EnvelopePayload,
            BackendJsonSerializerContext.Default.UserSnapshotEnvelope)
            ?? throw new InvalidDataException("The published test snapshot envelope is invalid.");
        var firstKnowledge = await database.UserRevisionKnowledge.GetAsync(
            user.UId, firstOrigin.DeviceId, firstOrigin.InstanceId, user.KeyEpoch);
        var secondKnowledge = await database.UserRevisionKnowledge.GetAsync(
            user.UId, secondOrigin.DeviceId, secondOrigin.InstanceId, user.KeyEpoch);
        var localKnowledge = await database.UserRevisionKnowledge.GetAsync(
            user.UId, localIdentity.LocalDeviceId, localIdentity.OriginInstanceId, user.KeyEpoch);
        var reloadedUser = await database.Users.GetByIdAsync(user.UId);

        MSTestAssert.IsTrue(merged);
        MSTestAssert.IsFalse(rows.Any(snapshot => snapshot.Status == UserSyncSnapshotStatus.Pending));
        MSTestAssert.HasCount(3, rows);
        MSTestAssert.AreEqual(2, rows.Count(snapshot => snapshot.Status == UserSyncSnapshotStatus.MergedReceipt));
        MSTestAssert.IsTrue(rows.Any(snapshot =>
            snapshot.OriginDeviceId == firstOrigin.DeviceId &&
            snapshot.Status == UserSyncSnapshotStatus.MergedReceipt));
        MSTestAssert.IsTrue(rows.Any(snapshot =>
            snapshot.OriginDeviceId == secondOrigin.DeviceId &&
            snapshot.Status == UserSyncSnapshotStatus.MergedReceipt));
        MSTestAssert.IsNotNull(firstKnowledge);
        MSTestAssert.IsNotNull(secondKnowledge);
        MSTestAssert.IsNotNull(localKnowledge);
        MSTestAssert.AreEqual(2L, firstKnowledge.HighestMergedRevision);
        MSTestAssert.AreEqual(5L, secondKnowledge.HighestMergedRevision);
        MSTestAssert.AreEqual(1L, localKnowledge.HighestMergedRevision);
        MSTestAssert.IsTrue(publishedEnvelope.Coverage.Any(item =>
            item.OriginDeviceId == firstOrigin.DeviceId && item.OriginRevision == 2));
        MSTestAssert.IsTrue(publishedEnvelope.Coverage.Any(item =>
            item.OriginDeviceId == secondOrigin.DeviceId && item.OriginRevision == 5));
        MSTestAssert.HasCount(1, queue.EnqueuedItems);
        MSTestAssert.AreEqual(SyncModelType.User, queue.EnqueuedItems[0].ModelType);
        MSTestAssert.AreEqual(user.UId, queue.EnqueuedItems[0].ModelId);
        MSTestAssert.AreEqual(1, activation.ActivatePendingSyncsCalls);
        MSTestAssert.IsNotNull(reloadedUser);
        CollectionAssert.AreEqual(new byte[] { 0xEE, 0x01 }, reloadedUser.EncryptedGeneralUserDataPayload);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task TryMergePendingAsync_InvalidOriginIsIsolatedWithoutDeletingUnrelatedValidCandidate()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var localSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var validSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var trustedButDifferentKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var invalidEnvelopeKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var localIdentity = CreateIdentity(Guid.NewGuid(), Guid.NewGuid(), localSigningKey);
        var validOrigin = (DeviceId: Guid.NewGuid(), InstanceId: Guid.NewGuid());
        var invalidOrigin = (DeviceId: Guid.NewGuid(), InstanceId: Guid.NewGuid());
        var user = await AddUserAndMembershipAsync(
            database,
            localIdentity,
            (validOrigin.DeviceId, validOrigin.InstanceId, validSigningKey),
            (invalidOrigin.DeviceId, invalidOrigin.InstanceId, trustedButDifferentKey));

        await AddPendingAsync(database, CreateEnvelope(user, validOrigin.DeviceId, validOrigin.InstanceId, 3, validSigningKey, 0x33));
        await AddPendingAsync(database, CreateEnvelope(user, invalidOrigin.DeviceId, invalidOrigin.InstanceId, 4, invalidEnvelopeKey, 0x44));
        await database.UnitOfWork.SaveChangesAsync();

        var lifecycle = new UserLifecycleCoordinator();
        var publisher = new UserSnapshotPublisherService(
            database.UserSyncSnapshots,
            database.UserSyncStates,
            database.UserRevisionKnowledge,
            localIdentity,
            database.UnitOfWork,
            lifecycle);
        var coordinator = new UserSnapshotMergeCoordinator(
            database.Users,
            CreateMembershipAuthorizationService(database, localIdentity),
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            new FakeUserDataBundleSyncService(),
            publisher,
            new FakeSyncQueueWriterService(),
            new FakeSyncQueueService(),
            database.UnitOfWork,
            lifecycle);

        using var key = EncryptionKey.Create();
        var merged = await coordinator.TryMergePendingAsync(user.UId, key);

        database.Db.ChangeTracker.Clear();
        var rows = await database.Db.UserSyncSnapshots.Where(snapshot => snapshot.UserId == user.UId).ToListAsync();
        var quarantined = rows.Single(snapshot => snapshot.Status == UserSyncSnapshotStatus.IsolatedCorrupt);
        MSTestAssert.IsTrue(merged);
        MSTestAssert.AreEqual(invalidOrigin.DeviceId, quarantined.OriginDeviceId);
        MSTestAssert.IsFalse(string.IsNullOrWhiteSpace(quarantined.QuarantineReason));
        MSTestAssert.IsFalse(rows.Any(snapshot =>
            snapshot.OriginDeviceId == validOrigin.DeviceId && snapshot.Status == UserSyncSnapshotStatus.Pending));
        MSTestAssert.IsTrue(rows.Any(snapshot =>
            snapshot.OriginDeviceId == validOrigin.DeviceId && snapshot.Status == UserSyncSnapshotStatus.MergedReceipt));
        MSTestAssert.IsFalse(rows.Any(snapshot => snapshot.Status == UserSyncSnapshotStatus.LocalPublished));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task TryMergePendingAsync_VerifiedCoverageOnlyMerge_DoesNotPublishOrQueueAcknowledgementEcho()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var localSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var originSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var localIdentity = CreateIdentity(Guid.NewGuid(), Guid.NewGuid(), localSigningKey);
        var origin = (DeviceId: Guid.NewGuid(), InstanceId: Guid.NewGuid());
        var user = await AddUserAndMembershipAsync(
            database,
            localIdentity,
            (origin.DeviceId, origin.InstanceId, originSigningKey));
        await AddPendingAsync(database, CreateEnvelope(user, origin.DeviceId, origin.InstanceId, 6, originSigningKey, 0x66));
        await database.UnitOfWork.SaveChangesAsync();

        var lifecycle = new UserLifecycleCoordinator();
        var queue = new FakeSyncQueueWriterService();
        var activation = new FakeSyncQueueService();
        var coordinator = new UserSnapshotMergeCoordinator(
            database.Users,
            CreateMembershipAuthorizationService(database, localIdentity),
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            new FakeUserDataBundleSyncService(),
            new UserSnapshotPublisherService(
                database.UserSyncSnapshots,
                database.UserSyncStates,
                database.UserRevisionKnowledge,
                localIdentity,
                database.UnitOfWork,
                lifecycle),
            queue,
            activation,
            database.UnitOfWork,
            lifecycle);

        using var key = EncryptionKey.Create();
        var merged = await coordinator.TryMergePendingAsync(user.UId, key);

        database.Db.ChangeTracker.Clear();
        var rows = await database.Db.UserSyncSnapshots
            .Where(snapshot => snapshot.UserId == user.UId)
            .ToListAsync();
        var knowledge = await database.UserRevisionKnowledge.GetAsync(
            user.UId,
            origin.DeviceId,
            origin.InstanceId,
            user.KeyEpoch);

        MSTestAssert.IsTrue(merged);
        MSTestAssert.IsTrue(rows.Any(snapshot =>
            snapshot.OriginDeviceId == origin.DeviceId &&
            snapshot.Status == UserSyncSnapshotStatus.MergedReceipt));
        MSTestAssert.IsFalse(rows.Any(snapshot => snapshot.Status == UserSyncSnapshotStatus.LocalPublished));
        MSTestAssert.IsNotNull(knowledge);
        MSTestAssert.AreEqual(6L, knowledge.HighestMergedRevision);
        MSTestAssert.HasCount(0, queue.EnqueuedItems);
        MSTestAssert.AreEqual(1, activation.ActivatePendingSyncsCalls);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task TryMergePendingAsync_WhenCanonicalCannotBeOpened_KeepsPendingAndDoesNotQueue()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var localSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var originSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var localIdentity = CreateIdentity(Guid.NewGuid(), Guid.NewGuid(), localSigningKey);
        var origin = (DeviceId: Guid.NewGuid(), InstanceId: Guid.NewGuid());
        var user = await AddUserAndMembershipAsync(database, localIdentity, (origin.DeviceId, origin.InstanceId, originSigningKey));
        var originalGeneral = user.EncryptedGeneralUserDataPayload.ToArray();
        await AddPendingAsync(database, CreateEnvelope(user, origin.DeviceId, origin.InstanceId, 9, originSigningKey, 0x99));
        await database.UnitOfWork.SaveChangesAsync();

        var lifecycle = new UserLifecycleCoordinator();
        var bundleSync = new FakeUserDataBundleSyncService
        {
            Handler = (_, _, _, _) => throw new CryptographicException("The active key cannot open canonical state.")
        };
        var queue = new FakeSyncQueueWriterService();
        var coordinator = new UserSnapshotMergeCoordinator(
            database.Users,
            CreateMembershipAuthorizationService(database, localIdentity),
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            bundleSync,
            new UserSnapshotPublisherService(
                database.UserSyncSnapshots,
                database.UserSyncStates,
                database.UserRevisionKnowledge,
                localIdentity,
                database.UnitOfWork,
                lifecycle),
            queue,
            new FakeSyncQueueService(),
            database.UnitOfWork,
            lifecycle);

        using var key = EncryptionKey.Create();
        var merged = await coordinator.TryMergePendingAsync(user.UId, key);

        database.Db.ChangeTracker.Clear();
        var pending = await database.Db.UserSyncSnapshots.SingleAsync(snapshot =>
            snapshot.UserId == user.UId && snapshot.Status == UserSyncSnapshotStatus.Pending);
        var reloadedUser = await database.Users.GetByIdAsync(user.UId);
        MSTestAssert.IsFalse(merged);
        MSTestAssert.AreEqual(9L, pending.OriginRevision);
        MSTestAssert.HasCount(0, queue.EnqueuedItems);
        MSTestAssert.IsNotNull(reloadedUser);
        CollectionAssert.AreEqual(originalGeneral, reloadedUser.EncryptedGeneralUserDataPayload);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task TryMergePendingAsync_CanonicalFailureWithHealthyRemote_AttributesLocalAndRetainsRecoveryCandidate()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var localSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var originSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var localIdentity = CreateIdentity(Guid.NewGuid(), Guid.NewGuid(), localSigningKey);
        var origin = (DeviceId: Guid.NewGuid(), InstanceId: Guid.NewGuid());
        var user = await AddUserAndMembershipAsync(
            database,
            localIdentity,
            (origin.DeviceId, origin.InstanceId, originSigningKey));
        await AddPendingAsync(database, CreateEnvelope(user, origin.DeviceId, origin.InstanceId, 7, originSigningKey, 0x77));
        await database.UnitOfWork.SaveChangesAsync();

        var lifecycle = new UserLifecycleCoordinator();
        var faultService = new UserSyncFaultService(database.UserSyncFaults);
        var bundleSync = new FakeUserDataBundleSyncService
        {
            Handler = (_, snapshots, _, _) => Task.FromResult(new UserSnapshotMergeBatchResult(
                CanonicalChanged: false,
                snapshots.Select(snapshot => new UserSnapshotMergeEntryResult(
                    snapshot.OriginDeviceId,
                    snapshot.OriginInstanceId,
                    snapshot.OriginRevision,
                    Verified: true,
                    VerificationState: UserDataVerificationState.Healthy,
                    DiagnosticCode: "healthy-recovery-evidence")).ToArray(),
                CanonicalState: UserDataVerificationState.RootIntegrityFailure,
                CanonicalFailedBlobs: UserDataBlobKind.All,
                CanonicalDiagnosticCode: "root-integrity-failed"))
        };
        var queue = new FakeSyncQueueWriterService();
        var coordinator = new UserSnapshotMergeCoordinator(
            database.Users,
            CreateMembershipAuthorizationService(database, localIdentity),
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            bundleSync,
            new UserSnapshotPublisherService(
                database.UserSyncSnapshots,
                database.UserSyncStates,
                database.UserRevisionKnowledge,
                localIdentity,
                database.UnitOfWork,
                lifecycle),
            queue,
            new FakeSyncQueueService(),
            database.UnitOfWork,
            lifecycle,
            syncFaults: faultService);

        using var key = EncryptionKey.Create();
        var merged = await coordinator.TryMergePendingAsync(
            user.UId,
            key,
            UserSyncKeyConfidence.ExplicitlyTrusted);

        database.Db.ChangeTracker.Clear();
        var retained = await database.UserSyncSnapshots.GetAsync(
            user.UId, origin.DeviceId, origin.InstanceId, user.KeyEpoch);
        var faults = await database.UserSyncFaults.ListForUserAsync(user.UId);

        MSTestAssert.IsFalse(merged);
        MSTestAssert.IsNotNull(retained);
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.RecoveryCandidate, retained.Status);
        MSTestAssert.HasCount(1, faults);
        MSTestAssert.AreEqual(UserSyncFaultScope.LocalCanonical, faults[0].Scope);
        MSTestAssert.AreEqual(UserSyncFaultKind.CanonicalRootIntegrityFailure, faults[0].Kind);
        MSTestAssert.AreEqual(UserSyncHealthStatus.AwaitingEvidence, faults[0].Status);
        MSTestAssert.IsTrue(faults[0].BlocksPublishing);
        MSTestAssert.HasCount(0, queue.EnqueuedItems);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task TryMergePendingAsync_HealthyCanonicalAndBadCandidate_IsolatesOnlyBadOrigin()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var localSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var goodSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var badSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var localIdentity = CreateIdentity(Guid.NewGuid(), Guid.NewGuid(), localSigningKey);
        var goodOrigin = (DeviceId: Guid.NewGuid(), InstanceId: Guid.NewGuid());
        var badOrigin = (DeviceId: Guid.NewGuid(), InstanceId: Guid.NewGuid());
        var user = await AddUserAndMembershipAsync(
            database,
            localIdentity,
            (goodOrigin.DeviceId, goodOrigin.InstanceId, goodSigningKey),
            (badOrigin.DeviceId, badOrigin.InstanceId, badSigningKey));
        await AddPendingAsync(database, CreateEnvelope(user, goodOrigin.DeviceId, goodOrigin.InstanceId, 3, goodSigningKey, 0x31));
        await AddPendingAsync(database, CreateEnvelope(user, badOrigin.DeviceId, badOrigin.InstanceId, 4, badSigningKey, 0x41));
        await database.UnitOfWork.SaveChangesAsync();

        var lifecycle = new UserLifecycleCoordinator();
        var faultService = new UserSyncFaultService(database.UserSyncFaults);
        var bundleSync = new FakeUserDataBundleSyncService
        {
            Handler = (_, snapshots, _, _) => Task.FromResult(new UserSnapshotMergeBatchResult(
                CanonicalChanged: false,
                snapshots.Select(snapshot => snapshot.OriginDeviceId == goodOrigin.DeviceId
                    ? new UserSnapshotMergeEntryResult(
                        snapshot.OriginDeviceId,
                        snapshot.OriginInstanceId,
                        snapshot.OriginRevision,
                        Verified: true,
                        VerificationState: UserDataVerificationState.Healthy,
                        DiagnosticCode: "healthy")
                    : new UserSnapshotMergeEntryResult(
                        snapshot.OriginDeviceId,
                        snapshot.OriginInstanceId,
                        snapshot.OriginRevision,
                        Verified: false,
                        FailureReason: "general-integrity-failed",
                        VerificationState: UserDataVerificationState.GeneralBlobFailure,
                        FailedBlobs: UserDataBlobKind.General,
                        DiagnosticCode: "general-integrity-failed")).ToArray()))
        };
        var coordinator = new UserSnapshotMergeCoordinator(
            database.Users,
            CreateMembershipAuthorizationService(database, localIdentity),
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            bundleSync,
            new UserSnapshotPublisherService(
                database.UserSyncSnapshots,
                database.UserSyncStates,
                database.UserRevisionKnowledge,
                localIdentity,
                database.UnitOfWork,
                lifecycle),
            new FakeSyncQueueWriterService(),
            new FakeSyncQueueService(),
            database.UnitOfWork,
            lifecycle,
            syncFaults: faultService);

        using var key = EncryptionKey.Create();
        var merged = await coordinator.TryMergePendingAsync(user.UId, key);

        database.Db.ChangeTracker.Clear();
        var rows = await database.Db.UserSyncSnapshots.Where(snapshot => snapshot.UserId == user.UId).ToListAsync();
        var faults = await database.UserSyncFaults.ListForUserAsync(user.UId);

        MSTestAssert.IsTrue(merged);
        MSTestAssert.IsTrue(rows.Any(snapshot =>
            snapshot.OriginDeviceId == goodOrigin.DeviceId &&
            snapshot.Status == UserSyncSnapshotStatus.MergedReceipt));
        MSTestAssert.IsTrue(rows.Any(snapshot =>
            snapshot.OriginDeviceId == badOrigin.DeviceId &&
            snapshot.Status == UserSyncSnapshotStatus.IsolatedCorrupt));
        MSTestAssert.IsFalse(rows.Any(snapshot => snapshot.Status == UserSyncSnapshotStatus.LocalPublished));
        MSTestAssert.HasCount(1, faults);
        MSTestAssert.AreEqual(UserSyncFaultScope.SnapshotOrigin, faults[0].Scope);
        MSTestAssert.AreEqual(badOrigin.DeviceId, faults[0].OriginDeviceId);
        MSTestAssert.AreEqual(UserSyncFaultKind.IncomingIntegrityFailure, faults[0].Kind);
        MSTestAssert.IsFalse(faults.Any(fault => fault.Scope == UserSyncFaultScope.LocalCanonical));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task TryMergePendingAsync_UnconfirmedPasswordFailingCanonicalAndAllCandidates_DoesNotPersistCorruptionFault()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var localSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var originSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var localIdentity = CreateIdentity(Guid.NewGuid(), Guid.NewGuid(), localSigningKey);
        var origin = (DeviceId: Guid.NewGuid(), InstanceId: Guid.NewGuid());
        var user = await AddUserAndMembershipAsync(
            database,
            localIdentity,
            (origin.DeviceId, origin.InstanceId, originSigningKey));
        await AddPendingAsync(database, CreateEnvelope(user, origin.DeviceId, origin.InstanceId, 8, originSigningKey, 0x88));
        await database.UnitOfWork.SaveChangesAsync();

        var lifecycle = new UserLifecycleCoordinator();
        var faultService = new UserSyncFaultService(database.UserSyncFaults);
        var bundleSync = new FakeUserDataBundleSyncService
        {
            Handler = (_, snapshots, _, _) => Task.FromResult(new UserSnapshotMergeBatchResult(
                CanonicalChanged: false,
                snapshots.Select(snapshot => new UserSnapshotMergeEntryResult(
                    snapshot.OriginDeviceId,
                    snapshot.OriginInstanceId,
                    snapshot.OriginRevision,
                    Verified: false,
                    FailureReason: "root-decrypt-failed",
                    VerificationState: UserDataVerificationState.RootDecryptFailure,
                    FailedBlobs: UserDataBlobKind.All,
                    DiagnosticCode: "root-decrypt-failed")).ToArray(),
                CanonicalState: UserDataVerificationState.RootDecryptFailure,
                CanonicalFailedBlobs: UserDataBlobKind.All,
                CanonicalDiagnosticCode: "root-decrypt-failed"))
        };
        var coordinator = new UserSnapshotMergeCoordinator(
            database.Users,
            CreateMembershipAuthorizationService(database, localIdentity),
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            bundleSync,
            new UserSnapshotPublisherService(
                database.UserSyncSnapshots,
                database.UserSyncStates,
                database.UserRevisionKnowledge,
                localIdentity,
                database.UnitOfWork,
                lifecycle),
            new FakeSyncQueueWriterService(),
            new FakeSyncQueueService(),
            database.UnitOfWork,
            lifecycle,
            syncFaults: faultService);

        using var key = EncryptionKey.Create();
        var merged = await coordinator.TryMergePendingAsync(
            user.UId,
            key,
            UserSyncKeyConfidence.UnconfirmedPassword);

        database.Db.ChangeTracker.Clear();
        var retained = await database.UserSyncSnapshots.GetAsync(
            user.UId, origin.DeviceId, origin.InstanceId, user.KeyEpoch);
        var faults = await database.UserSyncFaults.ListForUserAsync(user.UId);

        MSTestAssert.IsFalse(merged);
        MSTestAssert.IsNotNull(retained);
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.Pending, retained.Status);
        MSTestAssert.HasCount(0, faults);
    }

    private static async Task<User> AddUserAndMembershipAsync(
        SqliteIntegrationTestDatabase database,
        FakeDeviceIdentityService localIdentity,
        params (Guid DeviceId, Guid InstanceId, Key SigningKey)[] remoteDevices)
    {
        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            UId = Guid.NewGuid(),
            UsernameHash = Enumerable.Repeat((byte)0x01, 32).ToArray(),
            UsernameSalt = Enumerable.Repeat((byte)0x02, 32).ToArray(),
            PasswordSalt = [0x03],
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

        var localDevice = CreateTrustedDevice(localIdentity.LocalDeviceId, localIdentity.SignPublicKey);
        await database.Devices.AddAsync(localDevice);
        await AddLinkAsync(database, user.UId, localDevice.Id);
        await AddAuthorizationAsync(
            database,
            user,
            localIdentity.LocalDeviceId,
            localIdentity.OriginInstanceId,
            localIdentity.SignPublicKey,
            isGenesis: true);

        foreach (var remote in remoteDevices)
        {
            var publicKey = remote.SigningKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
            var device = CreateTrustedDevice(remote.DeviceId, publicKey);
            await database.Devices.AddAsync(device);
            await AddLinkAsync(database, user.UId, device.Id);
            await AddAuthorizationAsync(
                database,
                user,
                remote.DeviceId,
                remote.InstanceId,
                publicKey,
                isGenesis: false);
        }

        await database.UnitOfWork.SaveChangesAsync();
        database.Db.ChangeTracker.Clear();
        return await database.Users.GetByIdAsync(user.UId)
            ?? throw new InvalidOperationException("The test user could not be reloaded.");
    }

    private static UserMembershipAuthorizationService CreateMembershipAuthorizationService(
        SqliteIntegrationTestDatabase database,
        FakeDeviceIdentityService localIdentity) =>
        new(
            database.UserMembershipAuthorizations,
            database.UserOriginRemovalCutoffs,
            localIdentity);

    private static Task AddAuthorizationAsync(
        SqliteIntegrationTestDatabase database,
        User user,
        Guid deviceId,
        Guid originInstanceId,
        byte[] signingPublicKey,
        bool isGenesis)
    {
        var authorization = new UserMembershipAuthorization
        {
            UserId = user.UId,
            DeviceId = deviceId,
            OriginInstanceId = originInstanceId,
            SignPublicKey = signingPublicKey.ToArray(),
            SignPublicKeyHash = Hashing.SHA256Hash(signingPublicKey),
            AgreementPublicKeyHash = Hashing.SHA256Hash([0xA1, 0xA2, 0xA3]),
            TlsCertFingerprint = deviceId.ToString("N"),
            DeviceType = DeviceType.WindowsPc,
            StartedMembershipEpoch = user.MembershipEpoch,
            MinimumKeyEpoch = user.KeyEpoch,
            IsActive = true,
            IsGenesis = isGenesis,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        return database.UserMembershipAuthorizations.AddAsync(authorization);
    }

    private static Device CreateTrustedDevice(Guid id, byte[] signingPublicKey)
    {
        var device = new Device
        {
            Id = id,
            SignPublicKey = signingPublicKey.ToArray(),
            PublicKey = [0x01],
            TlsCertFingerprint = id.ToString("N"),
            IsTrusted = true,
            IsBlocked = false
        };
        device.GenerateIntegrityHash();
        return device;
    }

    private static async Task AddLinkAsync(SqliteIntegrationTestDatabase database, Guid userId, Guid deviceId)
    {
        var link = new UserDevice
        {
            UserId = userId,
            DeviceId = deviceId,
            IsSyncOn = true,
            IsDeleted = false,
            LastModifiedAt = DateTimeOffset.UtcNow
        };
        link.GenerateIntegrityHash();
        await database.UserDevices.AddAsync(link);
    }

    private static async Task AddPendingAsync(
        SqliteIntegrationTestDatabase database,
        UserSnapshotEnvelope envelope)
    {
        var serialized = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            BackendJsonSerializerContext.Default.UserSnapshotEnvelope);
        await database.UserSyncSnapshots.AddAsync(new UserSyncSnapshot
        {
            UserId = envelope.UserId,
            OriginDeviceId = envelope.OriginDeviceId,
            OriginInstanceId = envelope.OriginInstanceId,
            OriginRevision = envelope.OriginRevision,
            UserKeyEpoch = envelope.UserKeyEpoch,
            MembershipEpoch = envelope.MembershipEpoch,
            CreatedAtUtc = envelope.CreatedAtUtc,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            SnapshotHash = envelope.SnapshotHash.ToArray(),
            OriginSignPublicKey = envelope.OriginSignPublicKey.ToArray(),
            OriginSignature = envelope.OriginSignature.ToArray(),
            EnvelopePayload = serialized,
            Status = UserSyncSnapshotStatus.Pending
        });
    }

    private static UserSnapshotEnvelope CreateEnvelope(
        User user,
        Guid originDeviceId,
        Guid originInstanceId,
        long revision,
        Key signingKey,
        byte marker)
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
            User = payload
        };
        UserSnapshotEnvelopeUtil.FillOriginAuthentication(
            envelope,
            CreateIdentity(originDeviceId, originInstanceId, signingKey));
        return envelope;
    }

    private static FakeDeviceIdentityService CreateIdentity(Guid deviceId, Guid instanceId, Key signingKey) =>
        new()
        {
            LocalDeviceId = deviceId,
            OriginInstanceId = instanceId,
            SignPublicKey = signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            SignHandler = bytes => SignatureAlgorithm.Ed25519.Sign(signingKey, bytes)
        };
}

