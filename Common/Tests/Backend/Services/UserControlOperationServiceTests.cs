using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserControlOperationServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task KeyEpochOperation_LockedReceiverAppliesAndDuplicateIsIdempotent()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var author = CreateIdentity(signingKey);
        var canonical = CreateUser(keyEpoch: 1, marker: 0x10, savedKey: [0xAA, 0xBB]);
        var authorDevice = CreateTrustedDevice(author.LocalDeviceId, author.SignPublicKey);
        var link = new UserDevice
        {
            UserId = canonical.UId,
            DeviceId = authorDevice.Id,
            IsSyncOn = true,
            IsDeleted = false
        };
        link.GenerateIntegrityHash();

        await database.Users.AddAsync(canonical);
        await database.Devices.AddAsync(authorDevice);
        await database.UserDevices.AddAsync(link);
        await database.UnitOfWork.SaveChangesAsync();
        var lifecycle = new UserLifecycleCoordinator();
        var membership = new FakeUserMembershipAuthorizationService();
        var loginIdentities = new UserLoginIdentityProjectionService(
            database.Users,
            database.UserSyncSnapshots,
            membership,
            lifecycle,
            database.UnitOfWork,
            database.DeletedUserBarriers);
        await loginIdentities.SetCanonicalAsync(canonical, canonical.GetGeneralUserDataVersion());
        await database.UnitOfWork.SaveChangesAsync();

        var replacement = CreateReplacement(canonical, marker: 0x40);
        var envelope = CreateSignedKeyReplacement(replacement, author, originSequence: 1);
        var auth = new FakeAuthService();
        var service = new UserControlOperationInboxService(
            database.UserControlOperations,
            database.UserControlStates,
            database.Users,
            database.Devices,
            database.UserDevices,
            database.UserSyncSnapshots,
            lifecycle,
            new FakeInteractiveSessionStateService(auth),
            database.UnitOfWork,
            membership,
            database.UserMembershipAuthorizations,
            database.LocalUserDevices,
            new FakeDeviceIdentityService(),
            new FakeSyncRuntimeService(),
            loginIdentities: loginIdentities);

        var first = await service.StoreAndApplyAsync(envelope, authorDevice.Id);
        var duplicate = await service.StoreAndApplyAsync(envelope, authorDevice.Id);

        database.Db.ChangeTracker.Clear();
        var reloaded = await database.Db.Users.SingleAsync(user => user.UId == canonical.UId);
        var stored = await database.Db.UserControlOperations.SingleAsync(operation => operation.OperationId == envelope.OperationId);
        var loginIdentity = await database.Users.GetLoginIdentityStateAsync(canonical.UId);

        MSTestAssert.AreEqual(UserControlOperationReceiptState.Applied, first.State);
        MSTestAssert.AreEqual(UserControlOperationReceiptState.Applied, duplicate.State);
        MSTestAssert.AreEqual(2L, reloaded.KeyEpoch);
        MSTestAssert.AreEqual(replacement.MembershipEpoch, reloaded.MembershipEpoch);
        MSTestAssert.IsNull(reloaded.SavedKey);
        CollectionAssert.AreEqual(replacement.EncryptedPayload, reloaded.EncryptedPayload);
        CollectionAssert.AreEqual(replacement.IntegrityHash, reloaded.IntegrityHash);
        MSTestAssert.AreEqual(UserControlOperationStatus.Applied, stored.Status);
        MSTestAssert.IsNotNull(loginIdentity);
        MSTestAssert.AreEqual(replacement.KeyEpoch, loginIdentity!.KeyEpoch);
        MSTestAssert.AreEqual(replacement.GetGeneralUserDataVersion(), loginIdentity.Version);
        CollectionAssert.AreEqual(replacement.UsernameHash, loginIdentity.UsernameHash);
        CollectionAssert.AreEqual(replacement.UsernameSalt, loginIdentity.UsernameSalt);
        MSTestAssert.HasCount(1, auth.LogoutUserCalls);
        MSTestAssert.AreEqual(AuthSessionInvalidationReason.ProfilePasswordChanged, auth.LogoutUserCalls[0].Reason);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task SameOperationIdWithDifferentHash_DurablyQuarantinesControlPlane()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var author = CreateIdentity(signingKey);
        var canonical = CreateUser(keyEpoch: 1, marker: 0x10);
        var authorDevice = CreateTrustedDevice(author.LocalDeviceId, author.SignPublicKey);
        var link = new UserDevice { UserId = canonical.UId, DeviceId = authorDevice.Id, IsSyncOn = true };
        link.GenerateIntegrityHash();
        await database.Users.AddAsync(canonical);
        await database.Devices.AddAsync(authorDevice);
        await database.UserDevices.AddAsync(link);
        await database.UnitOfWork.SaveChangesAsync();

        var firstReplacement = CreateReplacement(canonical, marker: 0x40);
        var first = CreateSignedKeyReplacement(firstReplacement, author, originSequence: 1);
        var service = new UserControlOperationInboxService(
            database.UserControlOperations,
            database.UserControlStates,
            database.Users,
            database.Devices,
            database.UserDevices,
            database.UserSyncSnapshots,
            new UserLifecycleCoordinator(),
            new FakeInteractiveSessionStateService(new FakeAuthService()),
            database.UnitOfWork,
            new FakeUserMembershipAuthorizationService(),
            database.UserMembershipAuthorizations,
            database.LocalUserDevices,
            new FakeDeviceIdentityService(),
            new FakeSyncRuntimeService());
        await service.StoreAndApplyAsync(first, authorDevice.Id);

        var conflictingReplacement = CreateUser(keyEpoch: 2, marker: 0x70);
        conflictingReplacement.UId = canonical.UId;
        conflictingReplacement.GenerateIntegrityHash();
        var conflicting = CreateSignedKeyReplacement(conflictingReplacement, author, originSequence: 2);
        conflicting.OperationId = first.OperationId;
        UserControlOperationEnvelopeUtil.FillOriginAuthentication(conflicting, author);

        var receipt = await service.StoreAndApplyAsync(conflicting, authorDevice.Id);

        database.Db.ChangeTracker.Clear();
        var row = await database.Db.UserControlOperations.SingleAsync(operation => operation.OperationId == first.OperationId);
        var state = await database.Db.UserControlStates.SingleAsync(item => item.UserId == canonical.UId);
        MSTestAssert.AreEqual(UserControlOperationReceiptState.Quarantined, receipt.State);
        MSTestAssert.AreEqual(UserControlOperationStatus.Quarantined, row.Status);
        MSTestAssert.IsTrue(state.HasConflict);
        CollectionAssert.AreEqual(conflicting.OperationHash, row.ConflictingOperationHash);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task DifferentAuthorsClaimingSameBaseTransition_QuarantinesBothExactOperations()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var firstSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var secondSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var firstAuthor = CreateIdentity(firstSigningKey);
        var secondAuthor = CreateIdentity(secondSigningKey);
        var canonical = CreateUser(keyEpoch: 1, marker: 0x10);
        var firstDevice = CreateTrustedDevice(firstAuthor.LocalDeviceId, firstAuthor.SignPublicKey);
        var secondDevice = CreateTrustedDevice(secondAuthor.LocalDeviceId, secondAuthor.SignPublicKey);
        var firstLink = new UserDevice { UserId = canonical.UId, DeviceId = firstDevice.Id, IsSyncOn = true };
        var secondLink = new UserDevice { UserId = canonical.UId, DeviceId = secondDevice.Id, IsSyncOn = true };
        firstLink.GenerateIntegrityHash();
        secondLink.GenerateIntegrityHash();
        await database.Users.AddAsync(canonical);
        await database.Devices.AddAsync(firstDevice);
        await database.Devices.AddAsync(secondDevice);
        await database.UserDevices.AddAsync(firstLink);
        await database.UserDevices.AddAsync(secondLink);
        await database.UnitOfWork.SaveChangesAsync();

        var firstReplacement = CreateReplacement(canonical, marker: 0x41);
        var secondReplacement = CreateReplacement(canonical, marker: 0x71);
        var first = CreateSignedKeyReplacement(firstReplacement, firstAuthor, originSequence: 1);
        var second = CreateSignedKeyReplacement(secondReplacement, secondAuthor, originSequence: 1);
        var service = new UserControlOperationInboxService(
            database.UserControlOperations,
            database.UserControlStates,
            database.Users,
            database.Devices,
            database.UserDevices,
            database.UserSyncSnapshots,
            new UserLifecycleCoordinator(),
            new FakeInteractiveSessionStateService(new FakeAuthService()),
            database.UnitOfWork,
            new FakeUserMembershipAuthorizationService(),
            database.UserMembershipAuthorizations,
            database.LocalUserDevices,
            new FakeDeviceIdentityService(),
            new FakeSyncRuntimeService());

        MSTestAssert.AreEqual(
            UserControlOperationReceiptState.Applied,
            (await service.StoreAndApplyAsync(first, firstDevice.Id)).State);
        var conflictingReceipt = await service.StoreAndApplyAsync(second, secondDevice.Id);

        database.Db.ChangeTracker.Clear();
        var rows = await database.Db.UserControlOperations
            .Where(operation => operation.UserId == canonical.UId)
            .OrderBy(operation => operation.OperationId)
            .ToListAsync();
        var state = await database.Db.UserControlStates.SingleAsync(item => item.UserId == canonical.UId);

        MSTestAssert.AreEqual(UserControlOperationReceiptState.Quarantined, conflictingReceipt.State);
        MSTestAssert.HasCount(2, rows);
        MSTestAssert.IsTrue(rows.All(operation => operation.Status == UserControlOperationStatus.Quarantined));
        MSTestAssert.IsTrue(state.HasConflict);
        CollectionAssert.AreEquivalent(
            new[] { first.OperationId, second.OperationId },
            rows.Select(operation => operation.OperationId).ToArray());
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task StoredPendingOperation_CanBeAppliedAfterRestartWhenPrecedingEpochArrives()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var author = CreateIdentity(signingKey);
        var canonical = CreateUser(keyEpoch: 1, marker: 0x10);
        var authorDevice = CreateTrustedDevice(author.LocalDeviceId, author.SignPublicKey);
        var link = new UserDevice { UserId = canonical.UId, DeviceId = authorDevice.Id, IsSyncOn = true };
        link.GenerateIntegrityHash();
        await database.Users.AddAsync(canonical);
        await database.Devices.AddAsync(authorDevice);
        await database.UserDevices.AddAsync(link);
        await database.UnitOfWork.SaveChangesAsync();

        var epochTwo = CreateReplacement(canonical, marker: 0x20);
        var epochThree = CreateReplacement(epochTwo, marker: 0x30);
        var envelope = CreateSignedKeyReplacement(epochThree, author, originSequence: 2);
        var firstService = new UserControlOperationInboxService(
            database.UserControlOperations,
            database.UserControlStates,
            database.Users,
            database.Devices,
            database.UserDevices,
            database.UserSyncSnapshots,
            new UserLifecycleCoordinator(),
            new FakeInteractiveSessionStateService(new FakeAuthService()),
            database.UnitOfWork,
            new FakeUserMembershipAuthorizationService(),
            database.UserMembershipAuthorizations,
            database.LocalUserDevices,
            new FakeDeviceIdentityService(),
            new FakeSyncRuntimeService());

        var stored = await firstService.StoreAndApplyAsync(envelope, authorDevice.Id);
        MSTestAssert.AreEqual(UserControlOperationReceiptState.StoredPending, stored.State);

        UserControlOperationEnvelopeUtil.ApplyKeyEpochReplacementPayload(
            UserControlOperationEnvelopeUtil.CreateKeyEpochReplacementPayload(epochTwo, previousKeyEpoch: 1),
            canonical);
        database.Users.Update(canonical);
        await database.UnitOfWork.SaveChangesAsync();
        database.Db.ChangeTracker.Clear();

        var restartedService = new UserControlOperationInboxService(
            database.UserControlOperations,
            database.UserControlStates,
            database.Users,
            database.Devices,
            database.UserDevices,
            database.UserSyncSnapshots,
            new UserLifecycleCoordinator(),
            new FakeInteractiveSessionStateService(new FakeAuthService()),
            database.UnitOfWork,
            new FakeUserMembershipAuthorizationService(),
            database.UserMembershipAuthorizations,
            database.LocalUserDevices,
            new FakeDeviceIdentityService(),
            new FakeSyncRuntimeService());
        var applied = await restartedService.TryApplyStoredAsync(envelope.OperationId);

        database.Db.ChangeTracker.Clear();
        var reloaded = await database.Db.Users.SingleAsync(user => user.UId == canonical.UId);
        MSTestAssert.AreEqual(UserControlOperationReceiptState.Applied, applied.State);
        MSTestAssert.AreEqual(3L, reloaded.KeyEpoch);
        CollectionAssert.AreEqual(epochThree.EncryptedPayload, reloaded.EncryptedPayload);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task ResultingEpochAlreadyPresentWithDifferentCanonicalState_QuarantinesInsteadOfClaimingIdempotence()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var author = CreateIdentity(signingKey);
        var canonical = CreateUser(keyEpoch: 2, marker: 0x22);
        var authorDevice = CreateTrustedDevice(author.LocalDeviceId, author.SignPublicKey);
        var link = new UserDevice { UserId = canonical.UId, DeviceId = authorDevice.Id, IsSyncOn = true };
        link.GenerateIntegrityHash();
        await database.Users.AddAsync(canonical);
        await database.Devices.AddAsync(authorDevice);
        await database.UserDevices.AddAsync(link);
        await database.UnitOfWork.SaveChangesAsync();

        var differentReplacement = CreateUser(keyEpoch: 2, marker: 0x77);
        differentReplacement.UId = canonical.UId;
        differentReplacement.GenerateIntegrityHash();
        var envelope = CreateSignedKeyReplacement(differentReplacement, author, originSequence: 1);
        var auth = new FakeAuthService();
        var service = new UserControlOperationInboxService(
            database.UserControlOperations,
            database.UserControlStates,
            database.Users,
            database.Devices,
            database.UserDevices,
            database.UserSyncSnapshots,
            new UserLifecycleCoordinator(),
            new FakeInteractiveSessionStateService(auth),
            database.UnitOfWork,
            new FakeUserMembershipAuthorizationService(),
            database.UserMembershipAuthorizations,
            database.LocalUserDevices,
            new FakeDeviceIdentityService(),
            new FakeSyncRuntimeService());

        var receipt = await service.StoreAndApplyAsync(envelope, authorDevice.Id);

        database.Db.ChangeTracker.Clear();
        var reloaded = await database.Db.Users.SingleAsync(user => user.UId == canonical.UId);
        var row = await database.Db.UserControlOperations.SingleAsync(operation => operation.OperationId == envelope.OperationId);
        var state = await database.Db.UserControlStates.SingleAsync(item => item.UserId == canonical.UId);
        MSTestAssert.AreEqual(UserControlOperationReceiptState.Quarantined, receipt.State);
        MSTestAssert.AreEqual(UserControlOperationStatus.Quarantined, row.Status);
        MSTestAssert.IsTrue(state.HasConflict);
        CollectionAssert.AreEqual(canonical.EncryptedPayload, reloaded.EncryptedPayload);
        MSTestAssert.IsEmpty(auth.LogoutUserCalls);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task AppliedOperation_PersistsAfterCanonicalUserDeletion()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var identity = CreateIdentity(signingKey);
        var user = CreateUser(keyEpoch: 2, marker: 0x50);
        var authorization = CreateActiveAuthorization(user.UId, identity, minimumKeyEpoch: 1);
        await database.Users.AddAsync(user);
        await database.UserMembershipAuthorizations.AddAsync(authorization);
        await database.UnitOfWork.SaveChangesAsync();

        var writer = new UserControlOperationWriterService(
            database.UserControlOperations,
            database.UserControlStates,
            database.Users,
            database.Devices,
            database.UserDevices,
            database.UserMembershipAuthorizations,
            new FakeUserMembershipAuthorizationService(),
            identity,
            new UserLifecycleCoordinator(),
            database.UnitOfWork);
        var envelope = await writer.CreateAppliedKeyEpochReplacementAsync(user, previousKeyEpoch: 1);

        database.Users.Delete(user);
        await database.UnitOfWork.SaveChangesAsync();
        database.Db.ChangeTracker.Clear();

        MSTestAssert.IsFalse(await database.Db.Users.AnyAsync(candidate => candidate.UId == user.UId));
        var retained = await database.Db.UserControlOperations.SingleAsync(operation => operation.OperationId == envelope.OperationId);
        MSTestAssert.AreEqual(UserControlOperationStatus.Applied, retained.Status);
        CollectionAssert.AreEqual(envelope.OperationHash, retained.OperationHash);
    }

    private static FakeDeviceIdentityService CreateIdentity(Key signingKey)
    {
        var deviceId = Guid.NewGuid();
        return new FakeDeviceIdentityService
        {
            LocalDeviceId = deviceId,
            OriginInstanceId = Guid.NewGuid(),
            AgreementPublicKey = Enumerable.Repeat((byte)0x33, 32).ToArray(),
            SignPublicKey = signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            FingerprintHex = Convert.ToHexString(deviceId.ToByteArray().Concat(deviceId.ToByteArray()).ToArray()),
            SignHandler = bytes => SignatureAlgorithm.Ed25519.Sign(signingKey, bytes)
        };
    }

    private static UserMembershipAuthorization CreateActiveAuthorization(Guid userId, FakeDeviceIdentityService identity, long minimumKeyEpoch) =>
        new()
        {
            UserId = userId,
            DeviceId = identity.LocalDeviceId,
            OriginInstanceId = identity.OriginInstanceId,
            SignPublicKey = identity.SignPublicKey.ToArray(),
            SignPublicKeyHash = PasswordManagerLocal.Common.Backend.Security.Hashing.SHA256Hash(identity.SignPublicKey),
            AgreementPublicKeyHash = PasswordManagerLocal.Common.Backend.Security.Hashing.SHA256Hash(identity.AgreementPublicKey),
            TlsCertFingerprint = identity.FingerprintHex,
            DeviceType = identity.DeviceType,
            StartedMembershipEpoch = 1,
            MinimumKeyEpoch = minimumKeyEpoch,
            IsActive = true,
            IsGenesis = true
        };

    private static Device CreateTrustedDevice(Guid id, byte[] signPublicKey)
    {
        var device = new Device
        {
            Id = id,
            PublicKey = Enumerable.Repeat((byte)0x22, 32).ToArray(),
            SignPublicKey = signPublicKey.ToArray(),
            TlsCertFingerprint = Convert.ToHexString(id.ToByteArray().Concat(id.ToByteArray()).ToArray()),
            DeviceType = DeviceType.WindowsPc,
            IsTrusted = true,
            IsBlocked = false
        };
        device.GenerateIntegrityHash();
        return device;
    }

    private static User CreateUser(long keyEpoch, byte marker, byte[]? savedKey = null)
    {
        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            UId = Guid.NewGuid(),
            UsernameHash = Enumerable.Repeat(marker, 32).ToArray(),
            UsernameSalt = Enumerable.Repeat((byte)(marker ^ 0x5A), 32).ToArray(),
            PasswordSalt = [marker, 0x03],
            EncryptedPayload = [marker, 0x10],
            EncryptedGeneralUserDataPayload = [marker, 0x20],
            EncryptedUserPasswordsDataPayload = [marker, 0x30],
            EncryptedUserDevicesDataPayload = [marker, 0x40],
            SavedKey = savedKey,
            KeyEpoch = keyEpoch,
            MembershipEpoch = 1,
            GeneralDataVersionPhysicalTimeUnixMilliseconds = 1_000,
            GeneralDataVersionLogicalCounter = 0,
            GeneralDataVersionOriginDeviceId = Guid.Parse("B05F46DB-0F20-4B63-AF2E-9E88F231C57C"),
            GeneralDataVersionOriginInstanceId = Guid.Parse("54C33A5E-5091-4BB1-9517-FDA006123088"),
            LastModifiedAt = now,
            UserDataLastModifiedAt = now,
            GeneralUserDataLastModifiedAt = now,
            UserPasswordsDataLastModifiedAt = now,
            UserDevicesDataLastModifiedAt = now
        };
        user.GenerateIntegrityHash();
        return user;
    }

    private static User CreateReplacement(User canonical, byte marker)
    {
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        var replacement = new User
        {
            UId = canonical.UId,
            UsernameHash = canonical.UsernameHash.ToArray(),
            UsernameSalt = canonical.UsernameSalt.ToArray(),
            PasswordSalt = [marker, 0x03],
            EncryptedPayload = [marker, 0x10],
            EncryptedGeneralUserDataPayload = [marker, 0x20],
            EncryptedUserPasswordsDataPayload = [marker, 0x30],
            EncryptedUserDevicesDataPayload = [marker, 0x40],
            KeyEpoch = canonical.KeyEpoch + 1,
            MembershipEpoch = canonical.MembershipEpoch,
            GeneralDataVersionPhysicalTimeUnixMilliseconds = canonical.GeneralDataVersionPhysicalTimeUnixMilliseconds,
            GeneralDataVersionLogicalCounter = canonical.GeneralDataVersionLogicalCounter,
            GeneralDataVersionOriginDeviceId = canonical.GeneralDataVersionOriginDeviceId,
            GeneralDataVersionOriginInstanceId = canonical.GeneralDataVersionOriginInstanceId,
            LastModifiedAt = now,
            UserDataLastModifiedAt = now,
            GeneralUserDataLastModifiedAt = now,
            UserPasswordsDataLastModifiedAt = now,
            UserDevicesDataLastModifiedAt = now
        };
        replacement.GenerateIntegrityHash();
        return replacement;
    }

    private static UserControlOperationEnvelope CreateSignedKeyReplacement(
        User replacement,
        FakeDeviceIdentityService identity,
        long originSequence)
    {
        var previousKeyEpoch = replacement.KeyEpoch - 1;
        var payload = UserControlOperationEnvelopeUtil.CreateKeyEpochReplacementPayload(replacement, previousKeyEpoch);
        var envelope = new UserControlOperationEnvelope
        {
            OperationId = Guid.NewGuid(),
            UserId = replacement.UId,
            OperationType = UserControlOperationType.KeyEpochReplacement,
            OriginDeviceId = identity.LocalDeviceId,
            OriginInstanceId = identity.OriginInstanceId,
            OriginSequence = originSequence,
            PreviousKeyEpoch = previousKeyEpoch,
            ResultingKeyEpoch = replacement.KeyEpoch,
            PreviousMembershipEpoch = replacement.MembershipEpoch,
            ResultingMembershipEpoch = replacement.MembershipEpoch,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            OperationPayload = UserControlOperationEnvelopeUtil.SerializeKeyEpochReplacementPayload(payload)
        };
        UserControlOperationEnvelopeUtil.FillOriginAuthentication(envelope, identity);
        return envelope;
    }

}
