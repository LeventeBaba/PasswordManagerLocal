using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using NSecKey = NSec.Cryptography.Key;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Text.Json;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;

internal sealed class CoordinatorHarness : IDisposable
{
    private readonly RecoveryTestMaterial _material;
    private readonly NSecKey _localSigningKey;

    private CoordinatorHarness(
        Guid userId,
        EncryptionKey key,
        RecoveryTestMaterial material,
        NSecKey localSigningKey,
        FakeDeviceIdentityService localIdentity,
        UserDataRecoveryCoordinator coordinator,
        UserDataBundleVerificationService verification,
        FakeSyncQueueWriterService queue,
        ControllableQueueWriterService queueControl,
        FakeSyncQueueService activation,
        ControllablePendingActivationService activationControl,
        ControllableRecoveryPreparationService preparation,
        ControllableRecoveryStore recoveryStore,
        ControllableRecoveryUnitOfWork recoveryWork,
        RecordingVersionClock versionClock)
    {
        UserId = userId;
        Key = key;
        _material = material;
        _localSigningKey = localSigningKey;
        LocalIdentity = localIdentity;
        Coordinator = coordinator;
        Verification = verification;
        Queue = queue;
        QueueControl = queueControl;
        Activation = activation;
        ActivationControl = activationControl;
        Preparation = preparation;
        RecoveryStore = recoveryStore;
        RecoveryWork = recoveryWork;
        VersionClock = versionClock;
    }

    public Guid UserId { get; }
    public EncryptionKey Key { get; }
    public FakeDeviceIdentityService LocalIdentity { get; }
    public UserDataRecoveryCoordinator Coordinator { get; }
    public UserDataBundleVerificationService Verification { get; }
    public FakeSyncQueueWriterService Queue { get; }
    public ControllableQueueWriterService QueueControl { get; }
    public FakeSyncQueueService Activation { get; }
    public ControllablePendingActivationService ActivationControl { get; }

    public ControllableRecoveryPreparationService Preparation { get; }
    public ControllableRecoveryStore RecoveryStore { get; }
    public ControllableRecoveryUnitOfWork RecoveryWork { get; }
    public RecordingVersionClock VersionClock { get; }

    public static async Task<CoordinatorHarness> CreateAsync(
        SqliteRepositoryTestDatabase database,
        bool includeHealthyCandidate,
        IDatabaseHealthService? databaseHealth = null,
        bool failNextQueueWrite = false,
        bool failNextActivation = false,
        bool localInstallationAuthorized = true,
        bool useRealMembership = false,
        bool useDurableQueue = false)
    {
        var material = new RecoveryTestMaterial();
        var key = EncryptionKey.Create();
        var localSigning = NSecKey.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var localIdentity = RecoveryTestMaterial.CreateIdentity(
            Guid.Parse("A0000000-0000-0000-0000-000000000001"),
            Guid.Parse("A1000000-0000-0000-0000-000000000001"),
            localSigning);
        var remoteDevice = Guid.Parse("B0000000-0000-0000-0000-000000000002");
        var remoteInstance = Guid.Parse("B1000000-0000-0000-0000-000000000002");
        using var remoteSigning = NSecKey.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var remoteIdentity = RecoveryTestMaterial.CreateIdentity(remoteDevice, remoteInstance, remoteSigning);
        var integrity = new UserDataBundleIntegrityService();
        var verification = new UserDataBundleVerificationService(integrity);
        var faults = new UserSyncFaultService(database.UserSyncFaults);
        var canonicalHealth = new UserCanonicalHealthService(
            database.UserCanonicalCheckpoints,
            verification,
            faults,
            localIdentity,
            database.DeletedUserBarriers);
        var lifecycle = new UserLifecycleCoordinator();

        using (var localBundle = material.CreateBundle(
                   "damaged-local-user",
                   RecoveryTestMaterial.Version(1_000, localIdentity.LocalDeviceId, localIdentity.OriginInstanceId)))
        {
            var localUser = await material.EncryptUserAsync(localBundle, key);
            await database.Users.AddAsync(localUser);
            await database.UnitOfWork.SaveChangesAsync();
            await canonicalHealth.UpdateCheckpointAsync(localUser);
            await database.UnitOfWork.SaveChangesAsync();
            // Corrupt the ciphertext after committing the signed checkpoint.
            var corruptedPayload = localUser.EncryptedPayload.ToArray();
            corruptedPayload[0] ^= 0x5A;
            await database.UnitOfWork.ExecuteFixtureSqlAsync(
                """UPDATE "Users" SET "EncryptedPayload" = $payload WHERE "UId" = $user;""",
                ("$payload", corruptedPayload), ("$user", localUser.UId));
        }

        if (includeHealthyCandidate)
        {
            using var candidateBundle = material.CreateBundle(
                "remote-recovered-user",
                RecoveryTestMaterial.Version(2_000, remoteDevice, remoteInstance),
                [RecoveryTestMaterial.CreatePassword(
                    Guid.Parse("C0000000-0000-0000-0000-000000000003"),
                    "remote-password",
                    RecoveryTestMaterial.Version(2_100, remoteDevice, remoteInstance),
                    0x63)]);
            var envelope = await material.CreateEnvelopeAsync(candidateBundle, key, remoteIdentity, revision: 4);
            await database.UserSyncSnapshots.AddAsync(RecoveryTestMaterial.CreateSnapshotRow(envelope));
            await database.UnitOfWork.SaveChangesAsync();
        }

        IUserMembershipAuthorizationService membership = new FakeUserMembershipAuthorizationService
            { RejectLocalInstallation = !localInstallationAuthorized };
        if (useRealMembership)
        {
            localIdentity.AgreementPublicKey = Enumerable.Repeat((byte)0x31, 32).ToArray();
            localIdentity.FingerprintHex = new string('A', 64);
            remoteIdentity.AgreementPublicKey = Enumerable.Repeat((byte)0x32, 32).ToArray();
            remoteIdentity.FingerprintHex = new string('B', 64);
            var real = new UserMembershipAuthorizationService(database.UserMembershipAuthorizations,
                database.UserOriginRemovalCutoffs, localIdentity, database.UserControlOperations);
            var user = (await database.Users.GetByIdAsync(material.UserId))!;
            await real.CreateGenesisAsync(user);
            await new UserMembershipAuthorizationService(database.UserMembershipAuthorizations,
                database.UserOriginRemovalCutoffs, remoteIdentity, database.UserControlOperations).CreateGenesisAsync(user);
            await database.UnitOfWork.SaveChangesAsync();
            membership = real;
        }
        var preparation = new ControllableRecoveryPreparationService(new UserSnapshotBatchVerificationService(verification));
        var versionClock = new RecordingVersionClock();
        var bundleSync = new UserDataBundleSyncService(
            database.Users,
            integrity,
            new UserPasswordsDataMergeService(),
            new UserDevicesDataMergeService(),
            versionClock,
            canonicalHealth,
            verification,
            localIdentity,
            preparation);
        preparation.Inner = bundleSync;
        var recoveryStore = new ControllableRecoveryStore(new UserCanonicalRecoveryStore(database.UnitOfWork));
        var recoveryWork = new ControllableRecoveryUnitOfWork(database.UnitOfWork);
        var publisher = new UserSnapshotPublisherService(
            database.UserSyncSnapshots,
            database.UserSyncStates,
            database.UserRevisionKnowledge,
            localIdentity,
            database.UnitOfWork,
            lifecycle,
            database.DeletedUserBarriers,
            canonicalHealth,
            keyResolver: null,
            syncFaults: faults);
        var queue = new FakeSyncQueueWriterService();
        var activation = new FakeSyncQueueService();
        var activationControl = new ControllablePendingActivationService(activation)
        {
            FailNextActivation = failNextActivation
        };
        ISyncQueueWriterService queueWriter = queue;
        if (useDurableQueue)
        {
            var target = new Device
            {
                Id = remoteDevice, SignPublicKey = remoteIdentity.SignPublicKey.ToArray(),
                PublicKey = Enumerable.Repeat((byte)0x42, 32).ToArray(), TlsCertFingerprint = new string('B', 64),
                IsTrusted = true
            };
            target.GenerateIntegrityHash();
            await database.Devices.AddAsync(target);
            await database.UnitOfWork.SaveChangesAsync();
            var lifecycleItems = new SyncItemLifecycleService(database.SyncItems, database.SyncQueue,
                database.Users, database.Devices, database.UserDevices, database.Tombstones,
                new LocalDeviceMatcherService(localIdentity), canonicalHealth);
            queueWriter = new SyncQueueWriterService(database.SyncQueue, lifecycleItems,
                new FixedRecoveryTargetResolver(target), activationControl, database.Devices, localIdentity,
                new FakeSyncAuthorizationService(), database.UnitOfWork);
        }
        var queueControl = new ControllableQueueWriterService(queueWriter)
        {
            FailNextEnqueue = failNextQueueWrite
        };
        var loginIdentities = new UserLoginIdentityProjectionService(
            database.Users,
            database.UserSyncSnapshots,
            membership,
            lifecycle,
            database.UnitOfWork,
            database.DeletedUserBarriers,
            canonicalHealth);
        var coordinator = new UserDataRecoveryCoordinator(
            database.Users,
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            database.UserSyncFaults,
            faults,
            membership,
            preparation,
            canonicalHealth,
            database.UserCanonicalCheckpoints,
            database.UserControlStates,
            publisher,
            queueControl,
            activationControl,
            loginIdentities,
            localIdentity,
            recoveryWork,
            lifecycle,
            database.DeletedUserBarriers,
            new FakeInteractiveSessionStateService(),
            recoveryStore,
            databaseHealth ?? new DatabaseHealthService(database.Connections));

        return new CoordinatorHarness(
            material.UserId,
            key,
            material,
            localSigning,
            localIdentity,
            coordinator,
            verification,
            queue,
            queueControl,
            activation,
            activationControl,
            preparation,
            recoveryStore,
            recoveryWork,
            versionClock);
    }

    public async Task<PasswordManagerLocal.Common.Backend.Sync.Recovery.PreparedCanonicalRecovery> CreateNewerCanonicalAsync()
    {
        using var bundle = _material.CreateBundle("newer-local-user",
            RecoveryTestMaterial.Version(8_000, LocalIdentity.LocalDeviceId, LocalIdentity.OriginInstanceId),
            [RecoveryTestMaterial.CreatePassword(Guid.Parse("C0000000-0000-0000-0000-000000000099"),
                "newer-local-password", RecoveryTestMaterial.Version(8_100, LocalIdentity.LocalDeviceId, LocalIdentity.OriginInstanceId), 0x71)]);
        return new(await _material.EncryptUserAsync(bundle, Key), new(true, []));
    }

    public void Dispose()
    {
        Key.Dispose();
        _material.Dispose();
        _localSigningKey.Dispose();
    }
}
