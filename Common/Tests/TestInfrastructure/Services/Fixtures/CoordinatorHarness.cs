using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using NSecKey = NSec.Cryptography.Key;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
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
        ControllablePendingActivationService activationControl)
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

    public static async Task<CoordinatorHarness> CreateAsync(
        SqliteIntegrationTestDatabase database,
        bool includeHealthyCandidate,
        IDatabaseHealthService? databaseHealth = null,
        bool failNextQueueWrite = false,
        bool failNextActivation = false)
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
            // EncryptedPayload is an EF concurrency token. Replace the array instead of
            // mutating it in place so EF retains the actual persisted bytes as OriginalValue.
            // In-place mutation would also mutate EF's original-value snapshot and make the
            // intentional corruption UPDATE match zero rows.
            var corruptedPayload = localUser.EncryptedPayload.ToArray();
            corruptedPayload[0] ^= 0x5A;
            localUser.EncryptedPayload = corruptedPayload;
            database.Users.Update(localUser);
            await database.UnitOfWork.SaveChangesAsync();
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

        database.Db.ChangeTracker.Clear();
        var bundleSync = new UserDataBundleSyncService(
            database.Users,
            integrity,
            new UserPasswordsDataMergeService(),
            new UserDevicesDataMergeService(),
            new RecoveryTestVersionClock(),
            canonicalHealth,
            verification,
            database.UserCanonicalCheckpoints,
            localIdentity);
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
        var queueControl = new ControllableQueueWriterService(queue)
        {
            FailNextEnqueue = failNextQueueWrite
        };
        var activation = new FakeSyncQueueService();
        var activationControl = new ControllablePendingActivationService(activation)
        {
            FailNextActivation = failNextActivation
        };
        var loginIdentities = new UserLoginIdentityProjectionService(
            database.Users,
            database.UserSyncSnapshots,
            new FakeUserMembershipAuthorizationService(),
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
            new FakeUserMembershipAuthorizationService(),
            bundleSync,
            canonicalHealth,
            database.UserCanonicalCheckpoints,
            database.UserControlStates,
            publisher,
            queueControl,
            activationControl,
            loginIdentities,
            localIdentity,
            database.UnitOfWork,
            lifecycle,
            database.DeletedUserBarriers,
            new FakeInteractiveSessionStateService(),
            databaseHealth ?? new DatabaseHealthService(database.Db));

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
            activationControl);
    }

    public void Dispose()
    {
        Key.Dispose();
        _material.Dispose();
        _localSigningKey.Dispose();
    }
}
