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
using System.Text.Json;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserMembershipAuthorizationServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public void HistoricalGenesisRemoval_EmptyAdditionHashDeserializesWithoutRewritingSignedBytes()
    {
        var cutoff = new DeviceRemovalOriginCutoffPayload
        {
            AuthorizationId = Guid.NewGuid(), OriginInstanceId = Guid.NewGuid(), UserKeyEpoch = 1,
            SignPublicKeyHash = RandomNumberGenerator.GetBytes(32)
        };
        var payload = new DeviceRemovalPayload
        {
            UserId = Guid.NewGuid(), RemovedDeviceId = Guid.NewGuid(), KeyEpoch = 1,
            PreviousMembershipEpoch = 2, ResultingMembershipEpoch = 3, Origins = [cutoff]
        };
        UserControlOperationEnvelopeUtil.FinalizeDeviceRemovalPayload(payload);
        cutoff.AdditionOperationHash = [];
        var signedPayloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);

        var recovered = UserControlOperationEnvelopeUtil.DeserializeDeviceRemovalPayload(signedPayloadBytes);

        MSTestAssert.IsNull(recovered.Origins.Single().AdditionOperationId);
        MSTestAssert.IsNull(recovered.Origins.Single().AdditionOperationHash);
        CollectionAssert.AreEqual(payload.IntegrityHash, recovered.IntegrityHash);
        MSTestAssert.IsNotEmpty(signedPayloadBytes);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task RelayedAddition_UsesSignedOperationAsCanonicalAuthorizationIdentityAndEnrollmentTime()
    {
        await using var firstDatabase = await SqliteIntegrationTestDatabase.CreateAsync();
        await using var secondDatabase = await SqliteIntegrationTestDatabase.CreateAsync();
        using var firstSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var secondSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var firstIdentity = CreateIdentity(firstSigningKey);
        var secondIdentity = CreateIdentity(secondSigningKey);
        var userId = Guid.NewGuid();
        var addedDeviceId = Guid.NewGuid();
        var addedOriginId = Guid.NewGuid();
        var signPublicKey = Enumerable.Repeat((byte)0x52, 32).ToArray();
        var agreementPublicKey = Enumerable.Repeat((byte)0x62, 32).ToArray();
        var payload = UserControlOperationEnvelopeUtil.CreateDeviceAdditionPayload(
            userId, 1, 1, addedDeviceId, addedOriginId, signPublicKey, agreementPublicKey,
            new string('C', 64), DeviceType.AndroidMobile);
        var operationId = Guid.NewGuid();
        var operationHash = RandomNumberGenerator.GetBytes(32);
        var enrolledAtUtc = new DateTimeOffset(2026, 9, 30, 17, 45, 0, TimeSpan.Zero);

        var firstService = new UserMembershipAuthorizationService(
            firstDatabase.UserMembershipAuthorizations, firstDatabase.UserOriginRemovalCutoffs, firstIdentity);
        var secondService = new UserMembershipAuthorizationService(
            secondDatabase.UserMembershipAuthorizations, secondDatabase.UserOriginRemovalCutoffs, secondIdentity);

        var first = await firstService.AuthorizeAdditionAsync(payload, operationId, operationHash, enrolledAtUtc);
        var second = await secondService.AuthorizeAdditionAsync(payload, operationId, operationHash, enrolledAtUtc);

        MSTestAssert.AreEqual(operationId, first.AuthorizationId);
        MSTestAssert.AreEqual(operationId, second.AuthorizationId);
        MSTestAssert.AreEqual(first.AuthorizationId, second.AuthorizationId);
        MSTestAssert.AreEqual(enrolledAtUtc, first.CreatedAtUtc);
        MSTestAssert.AreEqual(enrolledAtUtc, second.CreatedAtUtc);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task LegacyReceiverLocalAuthorizationId_AcceptsRemovalByCanonicalAdditionIdentity()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var identity = CreateIdentity(signingKey);
        var additionOperationId = Guid.NewGuid();
        var legacyReceiverLocalAuthorizationId = Guid.NewGuid();
        var additionHash = RandomNumberGenerator.GetBytes(32);
        var authorization = new UserMembershipAuthorization
        {
            AuthorizationId = legacyReceiverLocalAuthorizationId,
            UserId = Guid.NewGuid(),
            DeviceId = Guid.NewGuid(),
            OriginInstanceId = Guid.NewGuid(),
            SignPublicKey = Enumerable.Repeat((byte)0x71, 32).ToArray(),
            SignPublicKeyHash = Hashing.SHA256Hash(Enumerable.Repeat((byte)0x71, 32).ToArray()),
            AgreementPublicKeyHash = Hashing.SHA256Hash(Enumerable.Repeat((byte)0x72, 32).ToArray()),
            TlsCertFingerprint = new string('D', 64),
            DeviceType = DeviceType.AndroidMobile,
            StartedMembershipEpoch = 2,
            MinimumKeyEpoch = 1,
            IsActive = true,
            IsGenesis = false,
            AdditionOperationId = additionOperationId,
            AdditionOperationHash = additionHash,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1)
        };
        var cutoff = CreateCutoff(authorization, keyEpoch: 1, snapshotRevision: 4, controlSequence: 6);
        var removal = new DeviceRemovalPayload
        {
            UserId = authorization.UserId,
            RemovedDeviceId = authorization.DeviceId,
            PreviousMembershipEpoch = 2,
            ResultingMembershipEpoch = 3,
            KeyEpoch = 1,
            Origins = [cutoff]
        };
        UserControlOperationEnvelopeUtil.FinalizeDeviceRemovalPayload(removal);
        var removalOperationId = Guid.NewGuid();
        var removalHash = RandomNumberGenerator.GetBytes(32);
        var removedAtUtc = new DateTimeOffset(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);
        var service = new UserMembershipAuthorizationService(
            database.UserMembershipAuthorizations, database.UserOriginRemovalCutoffs, identity);

        await service.EndAuthorizationAsync(authorization, removal, removalOperationId, removalHash, removedAtUtc);

        MSTestAssert.IsFalse(authorization.IsActive);
        MSTestAssert.AreEqual(removalOperationId, authorization.RemovalOperationId);
        MSTestAssert.AreEqual(removedAtUtc, authorization.EndedAtUtc);
        MSTestAssert.AreEqual(additionOperationId, cutoff.AuthorizationId);
        var retainedCutoff = database.Db.UserOriginRemovalCutoffs.Local.Single();
        MSTestAssert.AreEqual(additionOperationId, retainedCutoff.AuthorizationId);
        MSTestAssert.AreEqual(removedAtUtc, retainedCutoff.CreatedAtUtc);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task HistoricalAuthorization_UsesExactInstallationKeyAndSurvivesCurrentDeviceDeletion()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var substitutedKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var identity = CreateIdentity(signingKey);
        var user = CreateUser();
        var device = CreateDevice(identity.LocalDeviceId, identity.SignPublicKey, identity.AgreementPublicKey, identity.FingerprintHex);
        var link = new UserDevice { UserId = user.UId, DeviceId = device.Id, IsSyncOn = true, IsDeleted = false };
        link.GenerateIntegrityHash();
        await database.Users.AddAsync(user);
        await database.Devices.AddAsync(device);
        await database.UserDevices.AddAsync(link);

        var service = new UserMembershipAuthorizationService(database.UserMembershipAuthorizations, database.UserOriginRemovalCutoffs, identity);
        var genesis = await service.CreateGenesisAsync(user);
        await database.UnitOfWork.SaveChangesAsync();

        database.Devices.Delete(device);
        await database.UnitOfWork.SaveChangesAsync();
        database.Db.ChangeTracker.Clear();

        var valid = CreateSnapshot(user.UId, identity.LocalDeviceId, identity.OriginInstanceId, membershipEpoch: 1, keyEpoch: 1, revision: 1, signingKey);
        var verified = await service.VerifySnapshotAuthorAsync(valid);
        MSTestAssert.AreEqual(genesis.AuthorizationId, verified.AuthorizationId);
        MSTestAssert.IsFalse(await database.Db.Devices.AnyAsync(row => row.Id == device.Id));
        MSTestAssert.IsNotNull(await database.UserMembershipAuthorizations.GetByIdAsync(genesis.AuthorizationId));

        var substituted = CreateSnapshot(user.UId, identity.LocalDeviceId, identity.OriginInstanceId, 1, 1, 2, substitutedKey);
        await MSTestAssert.ThrowsExactlyAsync<InvalidDataException>(() => service.VerifySnapshotAuthorAsync(substituted));

        var neverAuthorized = CreateSnapshot(user.UId, Guid.NewGuid(), Guid.NewGuid(), 1, 1, 1, signingKey);
        await MSTestAssert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => service.VerifySnapshotAuthorAsync(neverAuthorized));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task RemovedOrigin_AcceptsExactSnapshotAndControlCutoffs_AndRejectsContentAboveThem()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var identity = CreateIdentity(signingKey);
        var user = CreateUser();
        await database.Users.AddAsync(user);
        var service = new UserMembershipAuthorizationService(database.UserMembershipAuthorizations, database.UserOriginRemovalCutoffs, identity);
        var authorization = await service.CreateGenesisAsync(user);
        await database.UnitOfWork.SaveChangesAsync();

        var operationId = Guid.NewGuid();
        var operationHash = Enumerable.Repeat((byte)0x5A, 32).ToArray();
        var removal = new DeviceRemovalPayload
        {
            UserId = user.UId,
            RemovedDeviceId = identity.LocalDeviceId,
            PreviousMembershipEpoch = 1,
            ResultingMembershipEpoch = 2,
            KeyEpoch = 2,
            Origins =
            [
                CreateCutoff(authorization, keyEpoch: 1, snapshotRevision: 3, controlSequence: 4),
                CreateCutoff(authorization, keyEpoch: 2, snapshotRevision: 7, controlSequence: 4)
            ]
        };
        UserControlOperationEnvelopeUtil.FinalizeDeviceRemovalPayload(removal);
        await service.EndAuthorizationAsync(authorization, removal, operationId, operationHash, DateTimeOffset.UtcNow);
        await database.UnitOfWork.SaveChangesAsync();
        database.Db.ChangeTracker.Clear();

        await service.VerifySnapshotAuthorAsync(CreateSnapshot(user.UId, identity.LocalDeviceId, identity.OriginInstanceId, 1, 1, 3, signingKey));
        await service.VerifySnapshotAuthorAsync(CreateSnapshot(user.UId, identity.LocalDeviceId, identity.OriginInstanceId, 1, 2, 7, signingKey));
        await MSTestAssert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
            service.VerifySnapshotAuthorAsync(CreateSnapshot(user.UId, identity.LocalDeviceId, identity.OriginInstanceId, 1, 1, 4, signingKey)));
        await MSTestAssert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
            service.VerifySnapshotAuthorAsync(CreateSnapshot(user.UId, identity.LocalDeviceId, identity.OriginInstanceId, 1, 2, 8, signingKey)));

        await service.VerifyControlAuthorAsync(CreateControl(user.UId, identity, signingKey, sequence: 4));
        await MSTestAssert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
            service.VerifyControlAuthorAsync(CreateControl(user.UId, identity, signingKey, sequence: 5)));

        var cutoffs = await database.UserOriginRemovalCutoffs.ListForOriginAsync(user.UId, identity.LocalDeviceId, identity.OriginInstanceId);
        MSTestAssert.HasCount(2, cutoffs);
        MSTestAssert.AreEqual(2L, (await database.UserMembershipAuthorizations.GetByIdAsync(authorization.AuthorizationId))!.MaximumKeyEpoch);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task Readdition_RejectsRemovedOriginAndAuthorizesOnlyANewInstallationOrigin()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var identity = CreateIdentity(signingKey);
        var user = CreateUser();
        await database.Users.AddAsync(user);
        var service = new UserMembershipAuthorizationService(database.UserMembershipAuthorizations, database.UserOriginRemovalCutoffs, identity);
        var genesis = await service.CreateGenesisAsync(user);
        var removal = new DeviceRemovalPayload
        {
            UserId = user.UId,
            RemovedDeviceId = identity.LocalDeviceId,
            PreviousMembershipEpoch = 1,
            ResultingMembershipEpoch = 2,
            KeyEpoch = 1,
            Origins = [CreateCutoff(genesis, keyEpoch: 1, snapshotRevision: 0, controlSequence: 0)]
        };
        UserControlOperationEnvelopeUtil.FinalizeDeviceRemovalPayload(removal);
        await service.EndAuthorizationAsync(genesis, removal, Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow);
        await database.UnitOfWork.SaveChangesAsync();

        var reusedOrigin = UserControlOperationEnvelopeUtil.CreateDeviceAdditionPayload(
            user.UId, 1, 2, identity.LocalDeviceId, identity.OriginInstanceId,
            identity.SignPublicKey, identity.AgreementPublicKey, identity.FingerprintHex, identity.DeviceType);
        await MSTestAssert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            service.AuthorizeAdditionAsync(reusedOrigin, Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow));

        var newOrigin = Guid.NewGuid();
        var readdition = UserControlOperationEnvelopeUtil.CreateDeviceAdditionPayload(
            user.UId, 1, 2, identity.LocalDeviceId, newOrigin,
            identity.SignPublicKey, identity.AgreementPublicKey, identity.FingerprintHex, identity.DeviceType);
        var authorized = await service.AuthorizeAdditionAsync(readdition, Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow);

        MSTestAssert.AreEqual(newOrigin, authorized.OriginInstanceId);
        MSTestAssert.IsTrue(authorized.IsActive);
        MSTestAssert.AreEqual(3L, authorized.StartedMembershipEpoch);
    }

    private static DeviceRemovalOriginCutoffPayload CreateCutoff(UserMembershipAuthorization authorization, long keyEpoch, long snapshotRevision, long controlSequence) =>
        new()
        {
            AuthorizationId = UserMembershipAuthorizationIdentity.GetCanonicalAuthorizationId(authorization),
            OriginInstanceId = authorization.OriginInstanceId,
            UserKeyEpoch = keyEpoch,
            HighestAcceptedSnapshotRevision = snapshotRevision,
            HighestAcceptedControlSequence = controlSequence,
            SignPublicKeyHash = authorization.SignPublicKeyHash.ToArray(),
            AdditionOperationId = authorization.AdditionOperationId,
            AdditionOperationHash = authorization.AdditionOperationHash?.ToArray()
        };

    private static UserControlOperationEnvelope CreateControl(Guid userId, FakeDeviceIdentityService identity, Key key, long sequence)
    {
        var targetSigningKey = Enumerable.Repeat((byte)0x31, 32).ToArray();
        var targetAgreementKey = Enumerable.Repeat((byte)0x41, 32).ToArray();
        var payload = UserControlOperationEnvelopeUtil.CreateDeviceAdditionPayload(
            userId,
            keyEpoch: 1,
            previousMembershipEpoch: 1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            targetSigningKey,
            targetAgreementKey,
            new string('B', 64),
            DeviceType.AndroidMobile);
        var envelope = new UserControlOperationEnvelope
        {
            OperationId = Guid.NewGuid(),
            UserId = userId,
            OperationType = UserControlOperationType.DeviceAddition,
            OriginDeviceId = identity.LocalDeviceId,
            OriginInstanceId = identity.OriginInstanceId,
            OriginSequence = sequence,
            PreviousKeyEpoch = 1,
            ResultingKeyEpoch = 1,
            PreviousMembershipEpoch = 1,
            ResultingMembershipEpoch = 2,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            OperationPayload = UserControlOperationEnvelopeUtil.SerializeDeviceAdditionPayload(payload)
        };
        UserControlOperationEnvelopeUtil.FillOriginAuthentication(envelope, identity);
        return envelope;
    }

    private static UserSnapshotEnvelope CreateSnapshot(Guid userId, Guid deviceId, Guid originId, long membershipEpoch, long keyEpoch, long revision, Key signingKey)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var payload = new UserSyncPayload
        {
            UId = userId,
            UsernameHash = Enumerable.Repeat((byte)0x01, 32).ToArray(), UsernameSalt = Enumerable.Repeat((byte)0x02, 32).ToArray(),
            GeneralUserDataVersion = new() { PhysicalTimeUnixMilliseconds = 1_000 + revision, LogicalCounter = 0, OriginDeviceId = deviceId, OriginInstanceId = originId },
            PasswordSalt = [0x03],
            EncryptedPayload = [0x04], EncryptedGeneralUserDataPayload = [0x05],
            EncryptedUserPasswordsDataPayload = [0x06], EncryptedUserDevicesDataPayload = [0x07],
            UserDataLastModifiedAt = createdAt, GeneralUserDataLastModifiedAt = createdAt,
            UserPasswordsDataLastModifiedAt = createdAt, UserDevicesDataLastModifiedAt = createdAt,
            DeviceIds = [deviceId]
        };
        payload.IntegrityHash = SyncCryptoUtil.CalculateUserHash(payload, createdAt.ToUnixTimeMilliseconds());
        var envelope = new UserSnapshotEnvelope
        {
            UserId = userId, OriginDeviceId = deviceId, OriginInstanceId = originId,
            OriginRevision = revision, UserKeyEpoch = keyEpoch, MembershipEpoch = membershipEpoch,
            CreatedAtUtc = createdAt, User = payload
        };
        var identity = CreateIdentity(signingKey, deviceId, originId);
        UserSnapshotEnvelopeUtil.FillOriginAuthentication(envelope, identity);
        return envelope;
    }

    private static FakeDeviceIdentityService CreateIdentity(Key key, Guid? deviceId = null, Guid? originId = null) =>
        new()
        {
            LocalDeviceId = deviceId ?? Guid.NewGuid(),
            OriginInstanceId = originId ?? Guid.NewGuid(),
            SignPublicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            AgreementPublicKey = Enumerable.Repeat((byte)0x22, 32).ToArray(),
            FingerprintHex = new string('A', 64),
            SignHandler = bytes => SignatureAlgorithm.Ed25519.Sign(key, bytes)
        };

    private static Device CreateDevice(Guid id, byte[] signKey, byte[] agreementKey, string fingerprint)
    {
        var device = new Device
        {
            Id = id, SignPublicKey = signKey.ToArray(), PublicKey = agreementKey.ToArray(),
            TlsCertFingerprint = fingerprint, DeviceType = DeviceType.WindowsPc, IsTrusted = true
        };
        device.GenerateIntegrityHash();
        return device;
    }

    private static User CreateUser()
    {
        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            UId = Guid.NewGuid(), UsernameHash = [1], UsernameSalt = [2], PasswordSalt = [3],
            EncryptedPayload = [4], EncryptedGeneralUserDataPayload = [5],
            EncryptedUserPasswordsDataPayload = [6], EncryptedUserDevicesDataPayload = [7],
            KeyEpoch = 1, MembershipEpoch = 1, LastModifiedAt = now, UserDataLastModifiedAt = now,
            GeneralUserDataLastModifiedAt = now, UserPasswordsDataLastModifiedAt = now, UserDevicesDataLastModifiedAt = now
        };
        user.GenerateIntegrityHash();
        return user;
    }
}
