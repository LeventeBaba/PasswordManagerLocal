using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Security.Cryptography;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;
namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class AuthoritativeAccountDeletionTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task LocalDeletion_AtomicallyRetainsSignedEvidence_AndRemovesMutableAccountState()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var identity = CreateIdentity(signingKey, Guid.NewGuid(), Guid.NewGuid(), 'A');
        var user = CreateUser(Guid.NewGuid(), savedKey: RandomNumberGenerator.GetBytes(32));
        var authorization = CreateAuthorization(user.UId, identity, isGenesis: true);
        var snapshotHash = RandomNumberGenerator.GetBytes(32);
        var localDevice = CreateDevice(identity);
        var userDevice = new UserDevice
        {
            UserId = user.UId,
            DeviceId = localDevice.Id,
            IsSyncOn = true,
            IsDeleted = false,
            LastModifiedAt = DateTimeOffset.UtcNow
        };
        userDevice.GenerateIntegrityHash();
        var accountOnlyGroup = new Group { EncryptedPayload = [8], LastModifiedAt = DateTimeOffset.UtcNow };
        accountOnlyGroup.GenerateIntegrityHash();
        user.Groups.Add(accountOnlyGroup);

        await database.Users.AddAsync(user);
        await database.Devices.AddAsync(localDevice);
        await database.UserDevices.AddAsync(userDevice);
        await database.UserMembershipAuthorizations.AddAsync(authorization);
        await database.UserControlStates.AddAsync(new UserControlState
        {
            UserId = user.UId,
            LocalOriginInstanceId = identity.OriginInstanceId,
            NextOriginSequence = 1,
            AppliedKeyEpoch = user.KeyEpoch,
            AppliedMembershipEpoch = user.MembershipEpoch
        });
        await database.UserSyncStates.AddAsync(new UserSyncState
        {
            UserId = user.UId,
            LocalOriginInstanceId = identity.OriginInstanceId,
            NextOriginRevision = 2
        });
        await database.UserRevisionKnowledge.AddAsync(new UserRevisionKnowledge
        {
            UserId = user.UId,
            OriginDeviceId = identity.LocalDeviceId,
            OriginInstanceId = identity.OriginInstanceId,
            UserKeyEpoch = user.KeyEpoch,
            HighestStoredRevision = 1,
            HighestStoredSnapshotHash = snapshotHash,
            HighestMergedRevision = 1
        });
        await database.UserSyncSnapshots.AddAsync(new UserSyncSnapshot
        {
            UserId = user.UId,
            OriginDeviceId = identity.LocalDeviceId,
            OriginInstanceId = identity.OriginInstanceId,
            OriginRevision = 1,
            UserKeyEpoch = user.KeyEpoch,
            MembershipEpoch = user.MembershipEpoch,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            SnapshotHash = snapshotHash,
            OriginSignPublicKey = identity.SignPublicKey.ToArray(),
            OriginSignature = RandomNumberGenerator.GetBytes(64),
            EnvelopePayload = [1],
            Status = UserSyncSnapshotStatus.LocalPublished
        });
        await database.SyncItems.AddAsync(new SyncItem
        {
            ModelId = user.UId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated
        });
        await database.SyncItems.AddAsync(new SyncItem
        {
            ModelId = userDevice.ModelId,
            ModelType = SyncModelType.UserDevice,
            ChangeType = SyncChangeType.Updated
        });
        await database.SyncItems.AddAsync(new SyncItem
        {
            ModelId = accountOnlyGroup.Id,
            ModelType = SyncModelType.Group,
            ChangeType = SyncChangeType.Updated
        });
        await database.SyncItems.AddAsync(new SyncItem
        {
            ModelId = localDevice.Id,
            ModelType = SyncModelType.Device,
            ChangeType = SyncChangeType.Updated
        });
        await database.UnitOfWork.SaveChangesAsync();

        var membership = new UserMembershipAuthorizationService(
            database.UserMembershipAuthorizations,
            database.UserOriginRemovalCutoffs,
            identity);
        var lifecycle = new UserLifecycleCoordinator();
        var writer = new UserControlOperationWriterService(
            database.UserControlOperations,
            database.UserControlStates,
            database.Users,
            database.Devices,
            database.UserDevices,
            database.UserMembershipAuthorizations,
            membership,
            identity,
            lifecycle,
            database.UnitOfWork);
        var auth = new FakeAuthService();
        var runtime = new FakeSyncRuntimeService();
        var enrollment = new FakeDeviceEnrollmentService();
        var deletion = new UserDeletionService(
            null!,
            database.Users,
            lifecycle,
            writer,
            database.UserControlStates,
            database.DeletedUserBarriers,
            new UserAccountDeletionCleanupService(database.Db),
            auth,
            runtime,
            database.UnitOfWork,
            enrollment);

        await deletion.DeleteUserAsync(user.UId);

        database.Db.ChangeTracker.Clear();
        var barrier = await database.DeletedUserBarriers.GetAsync(user.UId);
        var operation = (await database.UserControlOperations.ListForUserAsync(user.UId)).Single(row =>
            row.OperationType == UserControlOperationType.AccountDeletion);

        MSTestAssert.IsNull(await database.Users.GetByIdAsync(user.UId));
        MSTestAssert.IsNotNull(barrier);
        MSTestAssert.AreEqual(UserControlOperationStatus.Applied, operation.Status);
        MSTestAssert.AreEqual(operation.OperationId, barrier.DeletionOperationId);
        MSTestAssert.IsTrue(operation.OperationHash.SequenceEqual(barrier.OperationHash));
        MSTestAssert.HasCount(0, await database.UserSyncSnapshots.ListForUserAsync(user.UId));
        MSTestAssert.HasCount(0, await database.UserRevisionKnowledge.ListAsync(user.UId, user.KeyEpoch));
        MSTestAssert.IsNull(await database.UserSyncStates.GetAsync(user.UId));
        MSTestAssert.IsNull(await database.SyncItems.GetAsync(user.UId, SyncModelType.User));
        MSTestAssert.IsNull(await database.SyncItems.GetAsync(userDevice.ModelId, SyncModelType.UserDevice));
        MSTestAssert.IsNull(await database.SyncItems.GetAsync(accountOnlyGroup.Id, SyncModelType.Group));
        MSTestAssert.IsNull(await database.SyncItems.GetAsync(localDevice.Id, SyncModelType.Device));
        MSTestAssert.IsNull(await database.UserDevices.GetAsync(user.UId, localDevice.Id));
        MSTestAssert.IsNull(await database.Groups.GetByIdAsync(accountOnlyGroup.Id));
        MSTestAssert.IsNotNull(await database.Devices.GetByIdAsync(localDevice.Id));
        MSTestAssert.IsNotNull(await database.UserMembershipAuthorizations.GetByIdAsync(authorization.AuthorizationId));
        MSTestAssert.IsNotNull(await database.UserControlStates.GetAsync(user.UId));
        MSTestAssert.HasCount(1, auth.LogoutUserCalls);
        MSTestAssert.AreEqual(AuthSessionInvalidationReason.ProfileRemoved, auth.LogoutUserCalls[0].Reason);
        MSTestAssert.AreEqual(1, runtime.RefreshSyncEnabledCalls);
        MSTestAssert.AreEqual(1, enrollment.CancelEnrollmentCalls);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task RemoteDeletion_AppliesWithoutUserOrKey_RejectsOldSnapshot_AndRemainsRelayable()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var sourceSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var localSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var relayPeerSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var source = CreateIdentity(sourceSigningKey, Guid.NewGuid(), Guid.NewGuid(), 'B');
        var local = CreateIdentity(localSigningKey, Guid.NewGuid(), Guid.NewGuid(), 'C');
        var relayPeer = CreateIdentity(relayPeerSigningKey, Guid.NewGuid(), Guid.NewGuid(), 'D');
        var detachedUser = CreateUser(Guid.NewGuid(), savedKey: null);
        var sourceAuthorization = CreateAuthorization(detachedUser.UId, source, isGenesis: true);
        var relayAuthorization = CreateAuthorization(detachedUser.UId, relayPeer, isGenesis: false);
        var relayPeerDevice = CreateDevice(relayPeer);

        await database.UserMembershipAuthorizations.AddAsync(sourceAuthorization);
        await database.UserMembershipAuthorizations.AddAsync(relayAuthorization);
        await database.Devices.AddAsync(relayPeerDevice);
        await database.UnitOfWork.SaveChangesAsync();

        var payload = UserControlOperationEnvelopeUtil.CreateAccountDeletionPayload(
            detachedUser,
            [sourceAuthorization, relayAuthorization]);
        var envelope = CreateDeletionEnvelope(detachedUser, source, payload, originSequence: 1);
        var membership = new UserMembershipAuthorizationService(
            database.UserMembershipAuthorizations,
            database.UserOriginRemovalCutoffs,
            local);
        var auth = new FakeAuthService();
        var runtime = new FakeSyncRuntimeService();
        var inbox = new UserControlOperationInboxService(
            database.UserControlOperations,
            database.UserControlStates,
            database.Users,
            database.Devices,
            database.UserDevices,
            database.UserSyncSnapshots,
            new UserLifecycleCoordinator(),
            new FakeInteractiveSessionStateService(auth),
            database.UnitOfWork,
            membership,
            database.UserMembershipAuthorizations,
            database.LocalUserDevices,
            local,
            runtime,
            database.DeletedUserBarriers,
            new UserAccountDeletionCleanupService(database.Db));

        var first = await inbox.StoreAndApplyAsync(envelope, source.LocalDeviceId);
        var duplicate = await inbox.StoreAndApplyAsync(envelope, source.LocalDeviceId);

        database.Db.ChangeTracker.Clear();
        MSTestAssert.AreEqual(UserControlOperationReceiptState.Applied, first.State);
        MSTestAssert.AreEqual(UserControlOperationReceiptState.Applied, duplicate.State);
        MSTestAssert.IsNotNull(await database.DeletedUserBarriers.GetAsync(detachedUser.UId));
        MSTestAssert.IsNull(await database.Users.GetByIdAsync(detachedUser.UId));
        MSTestAssert.HasCount(1, await database.UserControlOperations.ListForUserAsync(detachedUser.UId));

        var staleSnapshot = CreateSnapshotEnvelope(detachedUser, source, originRevision: 1);
        var snapshotInbox = new UserSnapshotInboxService(
            database.Users,
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            local,
            database.UnitOfWork,
            new UserLifecycleCoordinator(),
            membership,
            database.DeletedUserBarriers);
        var staleReceipt = await snapshotInbox.StoreAsync(staleSnapshot, source.LocalDeviceId);
        MSTestAssert.AreEqual(UserSnapshotReceiptState.RejectedAccountDeleted, staleReceipt.State);
        MSTestAssert.HasCount(0, await database.UserSyncSnapshots.ListForUserAsync(detachedUser.UId));

        var deltaBuilder = new FakeOutgoingDeltaBuilderService
        {
            Result = new NetworkDelta { Payload = [1] }
        };
        var antiEntropy = new UserControlOperationAntiEntropyService(
            database.UserControlOperations,
            database.Devices,
            database.SyncRoutes,
            deltaBuilder,
            local,
            database.UserMembershipAuthorizations);
        var inventory = await antiEntropy.BuildInventoryAsync(relayPeer.LocalDeviceId);
        var advertised = inventory.Users.Single(user => Guid.Parse(user.UserId) == detachedUser.UId).Operations.Single();
        MSTestAssert.AreEqual(envelope.OperationId, Guid.Parse(advertised.OperationId));
        MSTestAssert.IsTrue(envelope.OperationHash.SequenceEqual(advertised.OperationHash.ToByteArray()));

        var emptyPeerInventory = new UserControlOperationInventoryExchangeRequest();
        emptyPeerInventory.Users.Add(new UserControlOperationUserInventory { UserId = detachedUser.UId.ToString("N") });
        var requests = antiEntropy.FindMissingOperations(emptyPeerInventory, inventory.Users);
        var relayed = await antiEntropy.BuildRequestedOperationDeltasAsync(relayPeer.LocalDeviceId, requests);
        MSTestAssert.HasCount(1, relayed);
        MSTestAssert.IsNotNull(deltaBuilder.LastControlOperation);
        MSTestAssert.AreEqual(envelope.OperationId, deltaBuilder.LastControlOperation!.OperationId);
        MSTestAssert.IsTrue(UserControlOperationEnvelopeUtil.Serialize(envelope).SequenceEqual(deltaBuilder.LastControlOperation!.EnvelopePayload));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task LocalDeletion_RollsBackAccountAndEvidence_WhenCleanupFailsBeforeCommit()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var identity = CreateIdentity(signingKey, Guid.NewGuid(), Guid.NewGuid(), 'E');
        var user = CreateUser(Guid.NewGuid(), savedKey: RandomNumberGenerator.GetBytes(32));
        var authorization = CreateAuthorization(user.UId, identity, isGenesis: true);

        await database.Users.AddAsync(user);
        await database.UserMembershipAuthorizations.AddAsync(authorization);
        await database.UserControlStates.AddAsync(new UserControlState
        {
            UserId = user.UId,
            LocalOriginInstanceId = identity.OriginInstanceId,
            NextOriginSequence = 1,
            AppliedKeyEpoch = user.KeyEpoch,
            AppliedMembershipEpoch = user.MembershipEpoch
        });
        await database.UnitOfWork.SaveChangesAsync();

        var membership = new UserMembershipAuthorizationService(
            database.UserMembershipAuthorizations,
            database.UserOriginRemovalCutoffs,
            identity);
        var lifecycle = new UserLifecycleCoordinator();
        var writer = new UserControlOperationWriterService(
            database.UserControlOperations,
            database.UserControlStates,
            database.Users,
            database.Devices,
            database.UserDevices,
            database.UserMembershipAuthorizations,
            membership,
            identity,
            lifecycle,
            database.UnitOfWork);
        var deletion = new UserDeletionService(
            null!,
            database.Users,
            lifecycle,
            writer,
            database.UserControlStates,
            database.DeletedUserBarriers,
            new ThrowingDeletionCleanupService(),
            new FakeAuthService(),
            new FakeSyncRuntimeService(),
            database.UnitOfWork);

        await MSTestAssert.ThrowsExactlyAsync<InvalidOperationException>(() => deletion.DeleteUserAsync(user.UId));

        database.Db.ChangeTracker.Clear();
        MSTestAssert.IsNotNull(await database.Users.GetByIdAsync(user.UId));
        MSTestAssert.IsNull(await database.DeletedUserBarriers.GetAsync(user.UId));
        MSTestAssert.IsFalse((await database.UserControlOperations.ListForUserAsync(user.UId))
            .Any(row => row.OperationType == UserControlOperationType.AccountDeletion));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task RemoteDeletion_CleanupFailureLeavesDurablePendingBarrier_AndBlocksUserLookup()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var sourceSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var localSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var source = CreateIdentity(sourceSigningKey, Guid.NewGuid(), Guid.NewGuid(), 'H');
        var local = CreateIdentity(localSigningKey, Guid.NewGuid(), Guid.NewGuid(), 'I');
        var user = CreateUser(Guid.NewGuid(), savedKey: RandomNumberGenerator.GetBytes(32));
        var sourceAuthorization = CreateAuthorization(user.UId, source, isGenesis: true);
        await database.Users.AddAsync(user);
        await database.UserMembershipAuthorizations.AddAsync(sourceAuthorization);
        await database.UnitOfWork.SaveChangesAsync();

        var membership = new UserMembershipAuthorizationService(
            database.UserMembershipAuthorizations,
            database.UserOriginRemovalCutoffs,
            local);
        var auth = new FakeAuthService();
        var inbox = new UserControlOperationInboxService(
            database.UserControlOperations,
            database.UserControlStates,
            database.Users,
            database.Devices,
            database.UserDevices,
            database.UserSyncSnapshots,
            new UserLifecycleCoordinator(),
            new FakeInteractiveSessionStateService(auth),
            database.UnitOfWork,
            membership,
            database.UserMembershipAuthorizations,
            database.LocalUserDevices,
            local,
            new FakeSyncRuntimeService(),
            database.DeletedUserBarriers,
            new ThrowingDeletionCleanupService());
        var envelope = CreateDeletionEnvelope(
            user,
            source,
            UserControlOperationEnvelopeUtil.CreateAccountDeletionPayload(user, [sourceAuthorization]),
            originSequence: 1);

        await MSTestAssert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            inbox.StoreAndApplyAsync(envelope, source.LocalDeviceId));

        var pendingFork = CreateDeletionEnvelope(
            user,
            source,
            UserControlOperationEnvelopeUtil.CreateAccountDeletionPayload(user, [sourceAuthorization]),
            originSequence: 1);
        pendingFork.OperationId = envelope.OperationId;
        UserControlOperationEnvelopeUtil.FillOriginAuthentication(pendingFork, source);
        var forkReceipt = await inbox.StoreAndApplyAsync(pendingFork, source.LocalDeviceId);

        database.Db.ChangeTracker.Clear();
        var stored = await database.UserControlOperations.GetByIdAsync(envelope.OperationId);
        MSTestAssert.AreEqual(UserControlOperationReceiptState.Quarantined, forkReceipt.State);
        MSTestAssert.IsNotNull(stored);
        MSTestAssert.AreEqual(UserControlOperationStatus.StoredPending, stored!.Status);
        MSTestAssert.IsNotNull(await database.DeletedUserBarriers.GetAsync(user.UId));
        MSTestAssert.IsNotNull(await database.Users.GetByIdAsync(user.UId));
        var failClosedLookup = new UserLookupService(database.Users, null!, null!, database.DeletedUserBarriers);
        MSTestAssert.IsNull(await failClosedLookup.GetUserByUidAsync(user.UId));
        MSTestAssert.HasCount(1, auth.LogoutUserCalls);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task RemoteDeletion_InvalidSignatureIsRejected_AndOperationIdForkIsQuarantined()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var sourceSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var localSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var source = CreateIdentity(sourceSigningKey, Guid.NewGuid(), Guid.NewGuid(), 'F');
        var local = CreateIdentity(localSigningKey, Guid.NewGuid(), Guid.NewGuid(), 'G');
        var detachedUser = CreateUser(Guid.NewGuid(), savedKey: null);
        var sourceAuthorization = CreateAuthorization(detachedUser.UId, source, isGenesis: true);
        await database.UserMembershipAuthorizations.AddAsync(sourceAuthorization);
        await database.UnitOfWork.SaveChangesAsync();

        var membership = new UserMembershipAuthorizationService(
            database.UserMembershipAuthorizations,
            database.UserOriginRemovalCutoffs,
            local);
        var inbox = new UserControlOperationInboxService(
            database.UserControlOperations,
            database.UserControlStates,
            database.Users,
            database.Devices,
            database.UserDevices,
            database.UserSyncSnapshots,
            new UserLifecycleCoordinator(),
            new FakeInteractiveSessionStateService(new FakeAuthService()),
            database.UnitOfWork,
            membership,
            database.UserMembershipAuthorizations,
            database.LocalUserDevices,
            local,
            new FakeSyncRuntimeService(),
            database.DeletedUserBarriers,
            new UserAccountDeletionCleanupService(database.Db));

        var payload = UserControlOperationEnvelopeUtil.CreateAccountDeletionPayload(detachedUser, [sourceAuthorization]);
        var invalidSignature = CreateDeletionEnvelope(detachedUser, source, payload, originSequence: 1);
        invalidSignature.OriginSignature[0] ^= 0x5A;
        var rejected = await inbox.StoreAndApplyAsync(invalidSignature, source.LocalDeviceId);
        MSTestAssert.AreEqual(UserControlOperationReceiptState.Rejected, rejected.State);
        MSTestAssert.HasCount(0, await database.UserControlOperations.ListForUserAsync(detachedUser.UId));

        var canonical = CreateDeletionEnvelope(
            detachedUser,
            source,
            UserControlOperationEnvelopeUtil.CreateAccountDeletionPayload(detachedUser, [sourceAuthorization]),
            originSequence: 1);
        var applied = await inbox.StoreAndApplyAsync(canonical, source.LocalDeviceId);
        MSTestAssert.AreEqual(UserControlOperationReceiptState.Applied, applied.State);

        var fork = CreateDeletionEnvelope(
            detachedUser,
            source,
            UserControlOperationEnvelopeUtil.CreateAccountDeletionPayload(detachedUser, [sourceAuthorization]),
            originSequence: 1);
        fork.OperationId = canonical.OperationId;
        UserControlOperationEnvelopeUtil.FillOriginAuthentication(fork, source);
        var quarantined = await inbox.StoreAndApplyAsync(fork, source.LocalDeviceId);

        var secondValidDeletion = CreateDeletionEnvelope(
            detachedUser,
            source,
            UserControlOperationEnvelopeUtil.CreateAccountDeletionPayload(detachedUser, [sourceAuthorization]),
            originSequence: 2);
        var secondApplied = await inbox.StoreAndApplyAsync(secondValidDeletion, source.LocalDeviceId);

        database.Db.ChangeTracker.Clear();
        var retained = await database.UserControlOperations.GetByIdAsync(canonical.OperationId);
        var retainedSecond = await database.UserControlOperations.GetByIdAsync(secondValidDeletion.OperationId);
        var barrier = await database.DeletedUserBarriers.GetAsync(detachedUser.UId);
        MSTestAssert.AreEqual(UserControlOperationReceiptState.Quarantined, quarantined.State);
        MSTestAssert.AreEqual(UserControlOperationReceiptState.Applied, secondApplied.State);
        MSTestAssert.IsNotNull(retained);
        MSTestAssert.AreEqual(UserControlOperationStatus.Applied, retained!.Status);
        MSTestAssert.IsNotNull(retainedSecond);
        MSTestAssert.AreEqual(UserControlOperationStatus.Applied, retainedSecond!.Status);
        MSTestAssert.IsNotNull(barrier);
        MSTestAssert.IsTrue(barrier!.HasConflict);
        MSTestAssert.IsNull(await database.Users.GetByIdAsync(detachedUser.UId));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task DeletionBarrier_BlocksRepositoryResurrection_AndGenericDeletionProtocol()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var userId = Guid.NewGuid();
        await database.DeletedUserBarriers.AddAsync(new DeletedUserBarrier
        {
            UserId = userId,
            DeletionOperationId = Guid.NewGuid(),
            DeletionGeneration = Guid.NewGuid(),
            OriginDeviceId = Guid.NewGuid(),
            OriginInstanceId = Guid.NewGuid(),
            OriginSequence = 1,
            KeyEpoch = 1,
            MembershipEpoch = 1,
            DeletedAtUtc = DateTimeOffset.UtcNow,
            AppliedAtUtc = DateTimeOffset.UtcNow,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow,
            OperationHash = RandomNumberGenerator.GetBytes(32),
            OriginSignPublicKey = RandomNumberGenerator.GetBytes(32),
            OriginSignature = RandomNumberGenerator.GetBytes(64)
        });
        await database.UnitOfWork.SaveChangesAsync();
        database.Db.ChangeTracker.Clear();

        await database.Users.AddAsync(CreateUser(userId, savedKey: null));
        await MSTestAssert.ThrowsExactlyAsync<InvalidOperationException>(() => database.UnitOfWork.SaveChangesAsync());
        database.UnitOfWork.ClearTrackedChanges();

        var legacyDeletion = new SyncDeltaPayload
        {
            ModelId = userId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Deleted
        };
        MSTestAssert.ThrowsExactly<InvalidDataException>(() => SyncCryptoUtil.ValidatePayloadShape(legacyDeletion));
    }

    private static UserControlOperationEnvelope CreateDeletionEnvelope(
        User user,
        FakeDeviceIdentityService author,
        AccountDeletionPayload payload,
        long originSequence)
    {
        var envelope = new UserControlOperationEnvelope
        {
            OperationId = Guid.NewGuid(),
            UserId = user.UId,
            OperationType = UserControlOperationType.AccountDeletion,
            OriginDeviceId = author.LocalDeviceId,
            OriginInstanceId = author.OriginInstanceId,
            OriginSequence = originSequence,
            PreviousKeyEpoch = user.KeyEpoch,
            ResultingKeyEpoch = user.KeyEpoch,
            PreviousMembershipEpoch = user.MembershipEpoch,
            ResultingMembershipEpoch = user.MembershipEpoch,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            OperationPayload = UserControlOperationEnvelopeUtil.SerializeAccountDeletionPayload(payload)
        };
        UserControlOperationEnvelopeUtil.FillOriginAuthentication(envelope, author);
        return envelope;
    }

    private static UserSnapshotEnvelope CreateSnapshotEnvelope(
        User user,
        FakeDeviceIdentityService author,
        long originRevision)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var userPayload = new UserSyncPayload
        {
            UId = user.UId,
            UsernameHash = user.UsernameHash.ToArray(),
            UsernameSalt = user.UsernameSalt.ToArray(),
            GeneralUserDataVersion = user.GetGeneralUserDataVersion(),
            PasswordSalt = user.PasswordSalt.ToArray(),
            EncryptedPayload = user.EncryptedPayload.ToArray(),
            EncryptedGeneralUserDataPayload = user.EncryptedGeneralUserDataPayload.ToArray(),
            EncryptedUserPasswordsDataPayload = user.EncryptedUserPasswordsDataPayload.ToArray(),
            EncryptedUserDevicesDataPayload = user.EncryptedUserDevicesDataPayload.ToArray(),
            UserDataLastModifiedAt = user.UserDataLastModifiedAt,
            GeneralUserDataLastModifiedAt = user.GeneralUserDataLastModifiedAt,
            UserPasswordsDataLastModifiedAt = user.UserPasswordsDataLastModifiedAt,
            UserDevicesDataLastModifiedAt = user.UserDevicesDataLastModifiedAt,
            DeviceIds = [author.LocalDeviceId]
        };
        userPayload.IntegrityHash = SyncCryptoUtil.CalculateUserHash(userPayload, createdAt.ToUnixTimeMilliseconds());
        var envelope = new UserSnapshotEnvelope
        {
            UserId = user.UId,
            OriginDeviceId = author.LocalDeviceId,
            OriginInstanceId = author.OriginInstanceId,
            OriginRevision = originRevision,
            UserKeyEpoch = user.KeyEpoch,
            MembershipEpoch = user.MembershipEpoch,
            CreatedAtUtc = createdAt,
            User = userPayload,
            Coverage =
            [
                new UserSnapshotCoverageEntry
                {
                    OriginDeviceId = author.LocalDeviceId,
                    OriginInstanceId = author.OriginInstanceId,
                    UserKeyEpoch = user.KeyEpoch,
                    OriginRevision = originRevision
                }
            ]
        };
        UserSnapshotEnvelopeUtil.FillOriginAuthentication(envelope, author);
        return envelope;
    }

    private static UserMembershipAuthorization CreateAuthorization(
        Guid userId,
        FakeDeviceIdentityService identity,
        bool isGenesis) =>
        new()
        {
            UserId = userId,
            DeviceId = identity.LocalDeviceId,
            OriginInstanceId = identity.OriginInstanceId,
            SignPublicKey = identity.SignPublicKey.ToArray(),
            SignPublicKeyHash = Hashing.SHA256Hash(identity.SignPublicKey),
            AgreementPublicKeyHash = Hashing.SHA256Hash(identity.AgreementPublicKey),
            TlsCertFingerprint = identity.FingerprintHex,
            DeviceType = identity.DeviceType,
            StartedMembershipEpoch = 1,
            MinimumKeyEpoch = 1,
            IsActive = true,
            IsGenesis = isGenesis
        };

    private static FakeDeviceIdentityService CreateIdentity(
        Key key,
        Guid deviceId,
        Guid originInstanceId,
        char fingerprintMarker) =>
        new()
        {
            LocalDeviceId = deviceId,
            OriginInstanceId = originInstanceId,
            SignPublicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            AgreementPublicKey = RandomNumberGenerator.GetBytes(32),
            FingerprintHex = new string(fingerprintMarker, 64),
            DeviceType = DeviceType.WindowsPc,
            IsSyncOn = true,
            SignHandler = bytes => SignatureAlgorithm.Ed25519.Sign(key, bytes)
        };

    private static Device CreateDevice(FakeDeviceIdentityService identity)
    {
        var device = new Device
        {
            Id = identity.LocalDeviceId,
            PublicKey = identity.AgreementPublicKey.ToArray(),
            SignPublicKey = identity.SignPublicKey.ToArray(),
            TlsCertFingerprint = identity.FingerprintHex,
            DeviceType = identity.DeviceType,
            IsTrusted = true,
            IsBlocked = false
        };
        device.GenerateIntegrityHash();
        return device;
    }


    private static User CreateUser(Guid userId, byte[]? savedKey)
    {
        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            UId = userId,
            UsernameHash = Enumerable.Repeat((byte)1, 32).ToArray(),
            UsernameSalt = Enumerable.Repeat((byte)2, 32).ToArray(),
            PasswordSalt = [3],
            EncryptedPayload = [4],
            EncryptedGeneralUserDataPayload = [5],
            EncryptedUserPasswordsDataPayload = [6],
            EncryptedUserDevicesDataPayload = [7],
            SavedKey = savedKey,
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
        return user;
    }
}
