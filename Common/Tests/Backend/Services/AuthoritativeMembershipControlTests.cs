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

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class AuthoritativeMembershipControlTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    [TestCategory("Security")]
    public void MembershipOperations_RequireSingleStep_AndAccountDeletionPreservesFinalEpochs()
    {
        var envelope = new UserControlOperationEnvelope
        {
            OperationId = Guid.NewGuid(), UserId = Guid.NewGuid(), OperationType = UserControlOperationType.DeviceAddition,
            OriginDeviceId = Guid.NewGuid(), OriginInstanceId = Guid.NewGuid(), OriginSequence = 1,
            PreviousKeyEpoch = 1, ResultingKeyEpoch = 1,
            PreviousMembershipEpoch = 1, ResultingMembershipEpoch = 3,
            CreatedAtUtc = DateTimeOffset.UtcNow, OperationPayload = [1]
        };
        MSTestAssert.ThrowsExactly<InvalidDataException>(() => UserControlOperationEnvelopeUtil.CalculateOperationHash(envelope));

        envelope.ResultingMembershipEpoch = 2;
        envelope.OperationType = UserControlOperationType.MembershipChange;
        MSTestAssert.ThrowsExactly<InvalidDataException>(() => UserControlOperationEnvelopeUtil.CalculateOperationHash(envelope));

        envelope.OperationType = UserControlOperationType.AccountDeletion;
        MSTestAssert.ThrowsExactly<InvalidDataException>(() => UserControlOperationEnvelopeUtil.CalculateOperationHash(envelope));

        envelope.ResultingMembershipEpoch = envelope.PreviousMembershipEpoch;
        var deletionHash = UserControlOperationEnvelopeUtil.CalculateOperationHash(envelope);
        MSTestAssert.HasCount(32, deletionHash);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task DatabaseConstraint_BlocksCrossTypeActiveTransitionsFromSameMembershipBase()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var userId = Guid.NewGuid();
        await database.UserControlOperations.AddAsync(CreateStoredRow(userId, UserControlOperationType.DeviceAddition, 1));
        await database.UserControlOperations.AddAsync(CreateStoredRow(userId, UserControlOperationType.DeviceRemoval, 1));

        await MSTestAssert.ThrowsExactlyAsync<DbUpdateException>(() => database.UnitOfWork.SaveChangesAsync());
        database.UnitOfWork.ClearTrackedChanges();

        var first = CreateStoredRow(userId, UserControlOperationType.DeviceAddition, 1);
        var second = CreateStoredRow(userId, UserControlOperationType.DeviceRemoval, 1);
        first.Status = UserControlOperationStatus.Quarantined;
        second.Status = UserControlOperationStatus.Quarantined;
        await database.UserControlOperations.AddAsync(first);
        await database.UserControlOperations.AddAsync(second);
        await database.UnitOfWork.SaveChangesAsync();
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task LockedTargetInstallation_AppliesSignedRemovalAndInvalidatesLocalRuntimeAfterCommit()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var sourceSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var targetSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var source = CreateIdentity(sourceSigningKey, Guid.NewGuid(), Guid.NewGuid(), 'A');
        var target = CreateIdentity(targetSigningKey, Guid.NewGuid(), Guid.NewGuid(), 'B');
        var user = CreateUser(membershipEpoch: 2, savedKey: [0xAA]);
        var targetDevice = CreateDevice(target);
        var localIdentity = new LocalDeviceIdentity
        {
            Id = target.LocalDeviceId,
            OriginInstanceId = target.OriginInstanceId,
            AgreementPrivateKeyBlob = [0x01],
            SignPrivateKeyBlob = [0x02],
            PFXCertificate = [0x03],
            DeviceType = target.DeviceType,
            IsSyncOn = true
        };
        localIdentity.GenerateIntegrityHash();
        var targetLink = new UserDevice { UserId = user.UId, DeviceId = target.LocalDeviceId, IsSyncOn = true, IsDeleted = false };
        targetLink.GenerateIntegrityHash();
        var localLink = new LocalUserDevice { UserId = user.UId, LocalDeviceIdentityId = target.LocalDeviceId, IsSyncOn = true };
        localLink.GenerateIntegrityHash();
        var sourceAuthorization = CreateAuthorization(user.UId, source, startedMembershipEpoch: 1, isGenesis: true);
        var additionOperationId = Guid.NewGuid();
        var additionOperationHash = Enumerable.Repeat((byte)0x25, 32).ToArray();
        var targetAuthorization = CreateAuthorization(user.UId, target, startedMembershipEpoch: 2, isGenesis: false);
        targetAuthorization.AdditionOperationId = additionOperationId;
        targetAuthorization.AdditionOperationHash = additionOperationHash;

        await database.Users.AddAsync(user);
        await database.Devices.AddAsync(targetDevice);
        await database.Db.LocalDeviceIdentities.AddAsync(localIdentity);
        await database.UserDevices.AddAsync(targetLink);
        await database.LocalUserDevices.AddAsync(localLink);
        await database.UserMembershipAuthorizations.AddAsync(sourceAuthorization);
        await database.UserMembershipAuthorizations.AddAsync(targetAuthorization);
        await database.UserControlStates.AddAsync(new UserControlState
        {
            UserId = user.UId, LocalOriginInstanceId = target.OriginInstanceId, NextOriginSequence = 1,
            AppliedKeyEpoch = 1, AppliedMembershipEpoch = 2
        });
        await database.UnitOfWork.SaveChangesAsync();

        var removalPayload = new DeviceRemovalPayload
        {
            UserId = user.UId,
            RemovedDeviceId = target.LocalDeviceId,
            PreviousMembershipEpoch = 2,
            ResultingMembershipEpoch = 3,
            KeyEpoch = 1,
            Origins =
            [
                new DeviceRemovalOriginCutoffPayload
                {
                    AuthorizationId = targetAuthorization.AuthorizationId,
                    OriginInstanceId = target.OriginInstanceId,
                    UserKeyEpoch = 1,
                    HighestAcceptedSnapshotRevision = 6,
                    HighestAcceptedControlSequence = 2,
                    SignPublicKeyHash = targetAuthorization.SignPublicKeyHash.ToArray(),
                    AdditionOperationId = additionOperationId,
                    AdditionOperationHash = additionOperationHash.ToArray()
                }
            ]
        };
        UserControlOperationEnvelopeUtil.FinalizeDeviceRemovalPayload(removalPayload);
        var removalEnvelope = CreateRemovalEnvelope(user.UId, source, removalPayload, originSequence: 1);
        var auth = new FakeAuthService();
        var runtime = new FakeSyncRuntimeService();
        var membershipService = new UserMembershipAuthorizationService(database.UserMembershipAuthorizations, database.UserOriginRemovalCutoffs, target);
        var inbox = new UserControlOperationInboxService(
            database.UserControlOperations, database.UserControlStates, database.Users, database.Devices,
            database.UserDevices, database.UserSyncSnapshots, new UserLifecycleCoordinator(), new FakeInteractiveSessionStateService(auth),
            database.UnitOfWork, membershipService, database.UserMembershipAuthorizations,
            database.LocalUserDevices, target, runtime);

        var receipt = await inbox.StoreAndApplyAsync(removalEnvelope, source.LocalDeviceId);

        database.Db.ChangeTracker.Clear();
        var reloadedUser = await database.Users.GetByIdAsync(user.UId);
        var reloadedTarget = await database.UserMembershipAuthorizations.GetByIdAsync(targetAuthorization.AuthorizationId);
        var reloadedLink = await database.UserDevices.GetAsync(user.UId, target.LocalDeviceId);
        var reloadedLocal = await database.LocalUserDevices.GetAsync(user.UId);
        MSTestAssert.AreEqual(UserControlOperationReceiptState.Applied, receipt.State);
        MSTestAssert.AreEqual(3L, reloadedUser!.MembershipEpoch);
        MSTestAssert.IsNull(reloadedUser.SavedKey);
        MSTestAssert.IsFalse(reloadedTarget!.IsActive);
        MSTestAssert.IsTrue(reloadedLink!.IsDeleted);
        MSTestAssert.IsFalse(reloadedLocal!.IsSyncOn);
        MSTestAssert.HasCount(1, auth.LogoutUserCalls);
        MSTestAssert.AreEqual(AuthSessionInvalidationReason.ProfileRemoved, auth.LogoutUserCalls[0].Reason);
        MSTestAssert.AreEqual(1, runtime.RefreshSyncEnabledCalls);
    }

    private static UserControlOperation CreateStoredRow(Guid userId, UserControlOperationType type, long previousMembershipEpoch) => new()
    {
        OperationId = Guid.NewGuid(), UserId = userId, OperationType = type,
        OriginDeviceId = Guid.NewGuid(), OriginInstanceId = Guid.NewGuid(), OriginSequence = Random.Shared.NextInt64(1, long.MaxValue),
        PreviousKeyEpoch = 1, ResultingKeyEpoch = 1,
        PreviousMembershipEpoch = previousMembershipEpoch, ResultingMembershipEpoch = previousMembershipEpoch + 1,
        CreatedAtUtc = DateTimeOffset.UtcNow, ReceivedAtUtc = DateTimeOffset.UtcNow,
        PayloadHash = Enumerable.Repeat((byte)1, 32).ToArray(), OperationHash = RandomNumberGenerator.GetBytes(32),
        OriginSignPublicKey = Enumerable.Repeat((byte)2, 32).ToArray(), OriginSignature = Enumerable.Repeat((byte)3, 64).ToArray(),
        EnvelopePayload = [4], Status = UserControlOperationStatus.StoredPending
    };

    private static UserControlOperationEnvelope CreateRemovalEnvelope(Guid userId, FakeDeviceIdentityService author, DeviceRemovalPayload payload, long originSequence)
    {
        var envelope = new UserControlOperationEnvelope
        {
            OperationId = Guid.NewGuid(), UserId = userId, OperationType = UserControlOperationType.DeviceRemoval,
            OriginDeviceId = author.LocalDeviceId, OriginInstanceId = author.OriginInstanceId, OriginSequence = originSequence,
            PreviousKeyEpoch = payload.KeyEpoch, ResultingKeyEpoch = payload.KeyEpoch,
            PreviousMembershipEpoch = payload.PreviousMembershipEpoch, ResultingMembershipEpoch = payload.ResultingMembershipEpoch,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            OperationPayload = UserControlOperationEnvelopeUtil.SerializeDeviceRemovalPayload(payload)
        };
        UserControlOperationEnvelopeUtil.FillOriginAuthentication(envelope, author);
        return envelope;
    }

    private static UserMembershipAuthorization CreateAuthorization(Guid userId, FakeDeviceIdentityService identity, long startedMembershipEpoch, bool isGenesis) => new()
    {
        UserId = userId, DeviceId = identity.LocalDeviceId, OriginInstanceId = identity.OriginInstanceId,
        SignPublicKey = identity.SignPublicKey.ToArray(), SignPublicKeyHash = Hashing.SHA256Hash(identity.SignPublicKey),
        AgreementPublicKeyHash = Hashing.SHA256Hash(identity.AgreementPublicKey), TlsCertFingerprint = identity.FingerprintHex,
        DeviceType = identity.DeviceType, StartedMembershipEpoch = startedMembershipEpoch, MinimumKeyEpoch = 1,
        IsActive = true, IsGenesis = isGenesis
    };

    private static FakeDeviceIdentityService CreateIdentity(Key key, Guid deviceId, Guid originId, char fingerprintMarker) => new()
    {
        LocalDeviceId = deviceId, OriginInstanceId = originId,
        SignPublicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey), AgreementPublicKey = RandomNumberGenerator.GetBytes(32),
        FingerprintHex = new string(fingerprintMarker, 64), DeviceType = DeviceType.WindowsPc,
        SignHandler = bytes => SignatureAlgorithm.Ed25519.Sign(key, bytes)
    };

    private static Device CreateDevice(FakeDeviceIdentityService identity)
    {
        var device = new Device
        {
            Id = identity.LocalDeviceId, PublicKey = identity.AgreementPublicKey.ToArray(), SignPublicKey = identity.SignPublicKey.ToArray(),
            TlsCertFingerprint = identity.FingerprintHex, DeviceType = identity.DeviceType, IsTrusted = true
        };
        device.GenerateIntegrityHash();
        return device;
    }

    private static User CreateUser(long membershipEpoch, byte[]? savedKey)
    {
        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            UId = Guid.NewGuid(), UsernameHash = [1], UsernameSalt = [2], PasswordSalt = [3],
            EncryptedPayload = [4], EncryptedGeneralUserDataPayload = [5], EncryptedUserPasswordsDataPayload = [6],
            EncryptedUserDevicesDataPayload = [7], SavedKey = savedKey, KeyEpoch = 1, MembershipEpoch = membershipEpoch,
            LastModifiedAt = now, UserDataLastModifiedAt = now, GeneralUserDataLastModifiedAt = now,
            UserPasswordsDataLastModifiedAt = now, UserDevicesDataLastModifiedAt = now
        };
        user.GenerateIntegrityHash();
        return user;
    }
}
