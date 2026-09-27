using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Text.Json;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class OutgoingDeltaBuilderServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task BuildUserDelta_ProducesSignedEncryptedPayloadThatRecipientCanValidate()
    {
        using var senderProvider = CreateIdentityProvider();
        using var recipientProvider = CreateIdentityProvider();
        var sender = CreateIdentity(senderProvider);
        var recipient = CreateIdentity(recipientProvider);
        await sender.InitializeAsync();
        await sender.SetSyncOnAsync(true);
        await recipient.InitializeAsync();
        var target = CreateTargetDevice(recipient);
        var users = new InMemoryUserRepository();
        var user = new User
        {
            UId = Guid.NewGuid(),
            UsernameHash = Enumerable.Repeat((byte)0x01, 32).ToArray(),
            UsernameSalt = Enumerable.Repeat((byte)0x02, 32).ToArray(),
            PasswordSalt = [5, 6],
            EncryptedPayload = [7, 8, 9],
            SavedKey = [99],
            KeyEpoch = 1,
            MembershipEpoch = 1,
            GeneralDataVersionPhysicalTimeUnixMilliseconds = 1_000,
            GeneralDataVersionLogicalCounter = 0,
            GeneralDataVersionOriginDeviceId = sender.LocalDeviceId,
            GeneralDataVersionOriginInstanceId = sender.OriginInstanceId,
            LastModifiedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var link = new UserDevice
        {
            UserId = user.UId,
            DeviceId = target.Id,
            Device = target,
            IsSyncOn = true,
            IsDeleted = false
        };
        link.GenerateIntegrityHash();
        user.UserDevices.Add(link);
        user.GenerateIntegrityHash();
        await users.AddAsync(user);
        var userDevices = new FakeUserDeviceRepository();
        var localUsers = new FakeLocalUserDeviceRepository();
        var builder = new OutgoingDeltaBuilderService(
            users,
            new FakeGroupRepository(),
            new FakeDeviceRepository(),
            userDevices,
            new FakeSyncRouteRepository(userDevices, localUsers),
            sender,
            new FakeUserSnapshotPublisherService(sender),
            new FakeUserMembershipAuthorizationService());
        var changedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var delta = await builder.BuildAsync(new SyncItem
        {
            ModelId = user.UId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated,
            ChangedAtTs = changedAt
        }, target);

        MSTestAssert.AreEqual(target.Id.ToString("N"), delta.RecipientDeviceId);
        MSTestAssert.AreEqual(sender.DeviceIdHex, delta.DeviceId);
        MSTestAssert.AreEqual(changedAt, delta.Ts);
        MSTestAssert.IsTrue(NetDeltaSigner.VerifySignature(delta));
        var plaintext = recipient.DecryptFromDevice(
            delta.Payload,
            delta.EphemeralPublicKey,
            delta.Nonce,
            delta.Tag,
            SyncCryptoUtil.BuildAssociatedData(delta));
        CollectionAssert.AreEqual(Hashing.SHA256Hash(plaintext), delta.PayloadHash);
        var payload = JsonSerializer.Deserialize<SyncDeltaPayload>(plaintext);
        MSTestAssert.IsNotNull(payload);
        SyncCryptoUtil.ValidatePayloadIntegrity(payload, delta.Ts);
        MSTestAssert.AreEqual(user.UId, payload.ModelId);
        MSTestAssert.AreEqual(SyncModelType.User, payload.ModelType);
        MSTestAssert.AreEqual(SyncChangeType.Updated, payload.ChangeType);
        MSTestAssert.IsNotNull(payload.UserSnapshot);
        CollectionAssert.AreEqual(user.EncryptedPayload, payload.UserSnapshot.User.EncryptedPayload);
        MSTestAssert.AreEqual(sender.LocalDeviceId, payload.UserSnapshot.OriginDeviceId);
        MSTestAssert.AreEqual(sender.OriginInstanceId, payload.UserSnapshot.OriginInstanceId);
        MSTestAssert.AreEqual(1L, payload.UserSnapshot.OriginRevision);
        MSTestAssert.IsTrue(payload.UserSnapshot.User.DeviceIds.Contains(sender.LocalDeviceId));
        MSTestAssert.IsTrue(payload.UserSnapshot.User.DeviceIds.Contains(target.Id));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task BuildDeletedDelta_DoesNotRequireDeletedModelToStillExist()
    {
        using var senderProvider = CreateIdentityProvider();
        using var recipientProvider = CreateIdentityProvider();
        var sender = CreateIdentity(senderProvider);
        var recipient = CreateIdentity(recipientProvider);
        await sender.InitializeAsync();
        await sender.SetSyncOnAsync(true);
        await recipient.InitializeAsync();
        var target = CreateTargetDevice(recipient);
        var userDevices = new FakeUserDeviceRepository();
        var localUsers = new FakeLocalUserDeviceRepository();
        var builder = new OutgoingDeltaBuilderService(
            new InMemoryUserRepository(),
            new FakeGroupRepository(),
            new FakeDeviceRepository(),
            userDevices,
            new FakeSyncRouteRepository(userDevices, localUsers),
            sender,
            new FakeUserSnapshotPublisherService(sender),
            new FakeUserMembershipAuthorizationService());
        var deletedId = Guid.NewGuid();

        var delta = await builder.BuildAsync(new SyncItem
        {
            ModelId = deletedId,
            ModelType = SyncModelType.Group,
            ChangeType = SyncChangeType.Deleted
        }, target);

        var plaintext = recipient.DecryptFromDevice(
            delta.Payload,
            delta.EphemeralPublicKey,
            delta.Nonce,
            delta.Tag,
            SyncCryptoUtil.BuildAssociatedData(delta));
        var payload = JsonSerializer.Deserialize<SyncDeltaPayload>(plaintext);
        MSTestAssert.IsNotNull(payload);
        MSTestAssert.AreEqual(deletedId, payload.ModelId);
        MSTestAssert.AreEqual(SyncModelType.Group, payload.ModelType);
        MSTestAssert.AreEqual(SyncChangeType.Deleted, payload.ChangeType);
        MSTestAssert.IsNull(payload.Group);
        SyncCryptoUtil.ValidatePayloadIntegrity(payload, delta.Ts);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task BuildDeletedUserDeviceDelta_ProducesPayloadThatRecipientCanValidate()
    {
        using var senderProvider = CreateIdentityProvider();
        using var recipientProvider = CreateIdentityProvider();
        var sender = CreateIdentity(senderProvider);
        var recipient = CreateIdentity(recipientProvider);
        await sender.InitializeAsync();
        await sender.SetSyncOnAsync(true);
        await recipient.InitializeAsync();
        var target = CreateTargetDevice(recipient);
        var userDevices = new FakeUserDeviceRepository();
        var localUsers = new FakeLocalUserDeviceRepository();
        var changedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var userDevice = new UserDevice
        {
            UserId = Guid.NewGuid(),
            DeviceId = target.Id,
            Device = target,
            IsSyncOn = false,
            IsDeleted = true,
            DeletedAt = DateTimeOffset.FromUnixTimeMilliseconds(changedAt),
            LastModifiedAt = DateTimeOffset.FromUnixTimeMilliseconds(changedAt)
        };
        userDevice.GenerateIntegrityHash();
        await userDevices.AddAsync(userDevice);
        var builder = new OutgoingDeltaBuilderService(
            new InMemoryUserRepository(),
            new FakeGroupRepository(),
            new FakeDeviceRepository(),
            userDevices,
            new FakeSyncRouteRepository(userDevices, localUsers),
            sender,
            new FakeUserSnapshotPublisherService(sender),
            new FakeUserMembershipAuthorizationService());

        var delta = await builder.BuildAsync(new SyncItem
        {
            ModelId = userDevice.ModelId,
            ModelType = SyncModelType.UserDevice,
            ChangeType = SyncChangeType.Deleted,
            ChangedAtTs = changedAt
        }, target);

        var plaintext = recipient.DecryptFromDevice(
            delta.Payload,
            delta.EphemeralPublicKey,
            delta.Nonce,
            delta.Tag,
            SyncCryptoUtil.BuildAssociatedData(delta));
        var payload = JsonSerializer.Deserialize<SyncDeltaPayload>(plaintext);

        MSTestAssert.IsNotNull(payload?.UserDevice);
        MSTestAssert.AreEqual(userDevice.ModelId, payload.ModelId);
        MSTestAssert.AreEqual(SyncModelType.UserDevice, payload.ModelType);
        MSTestAssert.AreEqual(SyncChangeType.Deleted, payload.ChangeType);
        MSTestAssert.AreEqual(userDevice.UserId, payload.UserDevice.UserId);
        MSTestAssert.AreEqual(target.Id, payload.UserDevice.DeviceId);
        MSTestAssert.IsTrue(payload.UserDevice.IsDeleted);
        MSTestAssert.IsFalse(payload.UserDevice.IsSyncOn);
        SyncCryptoUtil.ValidatePayloadIntegrity(payload, delta.Ts);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task BuildDeviceDelta_IncludesOnlyUsersWithEligibleTargetRoutes()
    {
        using var senderProvider = CreateIdentityProvider();
        using var recipientProvider = CreateIdentityProvider();
        var sender = CreateIdentity(senderProvider);
        var recipient = CreateIdentity(recipientProvider);
        await sender.InitializeAsync();
        await sender.SetSyncOnAsync(true);
        await recipient.InitializeAsync();
        var target = CreateTargetDevice(recipient);

        var eligibleUserId = Guid.NewGuid();
        var localSyncOffUserId = Guid.NewGuid();
        var targetSyncOffUserId = Guid.NewGuid();
        var sourceSyncOffUserId = Guid.NewGuid();
        var sourceDevice = new Device
        {
            Id = Guid.NewGuid(),
            PublicKey = [1],
            SignPublicKey = [2],
            TlsCertFingerprint = "source-device",
            DeviceType = DeviceType.WindowsPc,
            IsTrusted = true
        };

        AddSourceLink(sourceDevice, eligibleUserId, isSyncOn: true);
        AddSourceLink(sourceDevice, localSyncOffUserId, isSyncOn: true);
        AddSourceLink(sourceDevice, targetSyncOffUserId, isSyncOn: true);
        AddSourceLink(sourceDevice, sourceSyncOffUserId, isSyncOn: false);
        sourceDevice.GenerateIntegrityHash();

        var devices = new FakeDeviceRepository();
        devices.Seed(sourceDevice);
        var userDevices = new FakeUserDeviceRepository();
        var localUsers = new FakeLocalUserDeviceRepository();
        await localUsers.AddAsync(new LocalUserDevice { UserId = eligibleUserId, IsSyncOn = true });
        await localUsers.AddAsync(new LocalUserDevice { UserId = localSyncOffUserId, IsSyncOn = false });
        await localUsers.AddAsync(new LocalUserDevice { UserId = targetSyncOffUserId, IsSyncOn = true });
        await localUsers.AddAsync(new LocalUserDevice { UserId = sourceSyncOffUserId, IsSyncOn = true });
        await userDevices.AddAsync(CreateTargetLink(eligibleUserId, target.Id, isSyncOn: true));
        await userDevices.AddAsync(CreateTargetLink(localSyncOffUserId, target.Id, isSyncOn: true));
        await userDevices.AddAsync(CreateTargetLink(targetSyncOffUserId, target.Id, isSyncOn: false));
        await userDevices.AddAsync(CreateTargetLink(sourceSyncOffUserId, target.Id, isSyncOn: true));

        var builder = new OutgoingDeltaBuilderService(
            new InMemoryUserRepository(),
            new FakeGroupRepository(),
            devices,
            userDevices,
            new FakeSyncRouteRepository(userDevices, localUsers),
            sender,
            new FakeUserSnapshotPublisherService(sender),
            new FakeUserMembershipAuthorizationService());

        var delta = await builder.BuildAsync(new SyncItem
        {
            ModelId = sourceDevice.Id,
            ModelType = SyncModelType.Device,
            ChangeType = SyncChangeType.Updated
        }, target);

        var plaintext = recipient.DecryptFromDevice(
            delta.Payload,
            delta.EphemeralPublicKey,
            delta.Nonce,
            delta.Tag,
            SyncCryptoUtil.BuildAssociatedData(delta));
        var payload = JsonSerializer.Deserialize<SyncDeltaPayload>(plaintext);

        MSTestAssert.IsNotNull(payload?.Device);
        CollectionAssert.AreEquivalent(new[] { eligibleUserId }, payload.Device.UserIds);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task BuildUserSnapshotRelay_MergedReceiptPreservesImmutableOriginEnvelopeAndSignsOuterTransportAsRelay()
    {
        using var relayProvider = CreateIdentityProvider();
        using var recipientProvider = CreateIdentityProvider();
        using var originSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var relay = CreateIdentity(relayProvider);
        var recipient = CreateIdentity(recipientProvider);
        await relay.InitializeAsync();
        await relay.SetSyncOnAsync(true);
        await recipient.InitializeAsync();
        var target = CreateTargetDevice(recipient);
        var originDeviceId = Guid.NewGuid();
        var originInstanceId = Guid.NewGuid();
        var envelope = CreateSignedForeignEnvelope(originDeviceId, originInstanceId, originSigningKey);
        var trustedOrigin = new Device
        {
            Id = originDeviceId,
            SignPublicKey = originSigningKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            PublicKey = new byte[32],
            TlsCertFingerprint = new string('A', 64),
            IsTrusted = true,
            IsBlocked = false
        };
        trustedOrigin.GenerateIntegrityHash();
        var devices = new FakeDeviceRepository();
        devices.Seed(trustedOrigin);
        var serializedEnvelope = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            BackendJsonSerializerContext.Default.UserSnapshotEnvelope);
        var stored = new UserSyncSnapshot
        {
            UserId = envelope.UserId,
            OriginDeviceId = envelope.OriginDeviceId,
            OriginInstanceId = envelope.OriginInstanceId,
            OriginRevision = envelope.OriginRevision,
            UserKeyEpoch = envelope.UserKeyEpoch,
            MembershipEpoch = envelope.MembershipEpoch,
            CreatedAtUtc = envelope.CreatedAtUtc,
            SnapshotHash = envelope.SnapshotHash.ToArray(),
            OriginSignPublicKey = envelope.OriginSignPublicKey.ToArray(),
            OriginSignature = envelope.OriginSignature.ToArray(),
            EnvelopePayload = serializedEnvelope,
            Status = UserSyncSnapshotStatus.MergedReceipt
        };
        var userDevices = new FakeUserDeviceRepository();
        var localUsers = new FakeLocalUserDeviceRepository();
        var builder = new OutgoingDeltaBuilderService(
            new InMemoryUserRepository(),
            new FakeGroupRepository(),
            devices,
            userDevices,
            new FakeSyncRouteRepository(userDevices, localUsers),
            relay,
            new FakeUserSnapshotPublisherService(relay),
            new FakeUserMembershipAuthorizationService());

        var delta = await builder.BuildUserSnapshotRelayAsync(stored, target);

        MSTestAssert.AreEqual(relay.DeviceIdHex, delta.DeviceId);
        MSTestAssert.AreEqual(target.Id.ToString("N"), delta.RecipientDeviceId);
        MSTestAssert.AreEqual(originDeviceId, delta.SnapshotOriginDeviceId);
        MSTestAssert.AreEqual(originInstanceId, delta.SnapshotOriginInstanceId);
        MSTestAssert.AreEqual(envelope.OriginRevision, delta.SnapshotOriginRevision);
        CollectionAssert.AreEqual(envelope.SnapshotHash, delta.SnapshotHash);
        MSTestAssert.IsTrue(NetDeltaSigner.VerifySignature(delta));

        var plaintext = recipient.DecryptFromDevice(
            delta.Payload,
            delta.EphemeralPublicKey,
            delta.Nonce,
            delta.Tag,
            SyncCryptoUtil.BuildAssociatedData(delta));
        var payload = JsonSerializer.Deserialize(
            plaintext,
            BackendJsonSerializerContext.Default.SyncDeltaPayload);
        MSTestAssert.IsNotNull(payload?.UserSnapshot);
        var relayed = payload!.UserSnapshot!;
        MSTestAssert.AreEqual(envelope.UserId, relayed.UserId);
        MSTestAssert.AreEqual(envelope.OriginDeviceId, relayed.OriginDeviceId);
        MSTestAssert.AreEqual(envelope.OriginInstanceId, relayed.OriginInstanceId);
        MSTestAssert.AreEqual(envelope.OriginRevision, relayed.OriginRevision);
        CollectionAssert.AreEqual(envelope.SnapshotHash, relayed.SnapshotHash);
        CollectionAssert.AreEqual(envelope.OriginSignature, relayed.OriginSignature);
        CollectionAssert.AreEqual(envelope.OriginSignPublicKey, relayed.OriginSignPublicKey);
        UserSnapshotEnvelopeUtil.VerifyWithSigningKey(relayed, trustedOrigin.SignPublicKey);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task BuildUserControlOperationRelay_PreservesOriginalAuthorAndExactSignedEnvelope()
    {
        using var relayProvider = CreateIdentityProvider();
        using var recipientProvider = CreateIdentityProvider();
        using var originSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var relay = CreateIdentity(relayProvider);
        var recipient = CreateIdentity(recipientProvider);
        await relay.InitializeAsync();
        await relay.SetSyncOnAsync(true);
        await recipient.InitializeAsync();
        var target = CreateTargetDevice(recipient);
        var originDeviceId = Guid.NewGuid();
        var originInstanceId = Guid.NewGuid();
        var originIdentity = new FakeDeviceIdentityService
        {
            LocalDeviceId = originDeviceId,
            OriginInstanceId = originInstanceId,
            SignPublicKey = originSigningKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            SignHandler = data => SignatureAlgorithm.Ed25519.Sign(originSigningKey, data)
        };
        var replacement = CreateControlReplacementUser();
        var operationPayload = UserControlOperationEnvelopeUtil.CreateKeyEpochReplacementPayload(replacement, 1);
        var envelope = new UserControlOperationEnvelope
        {
            OperationId = Guid.NewGuid(),
            UserId = replacement.UId,
            OperationType = UserControlOperationType.KeyEpochReplacement,
            OriginDeviceId = originDeviceId,
            OriginInstanceId = originInstanceId,
            OriginSequence = 7,
            PreviousKeyEpoch = 1,
            ResultingKeyEpoch = 2,
            PreviousMembershipEpoch = 1,
            ResultingMembershipEpoch = 1,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            OperationPayload = UserControlOperationEnvelopeUtil.SerializeKeyEpochReplacementPayload(operationPayload)
        };
        UserControlOperationEnvelopeUtil.FillOriginAuthentication(envelope, originIdentity);
        var trustedOrigin = new Device
        {
            Id = originDeviceId,
            SignPublicKey = originIdentity.SignPublicKey.ToArray(),
            PublicKey = new byte[32],
            TlsCertFingerprint = new string('B', 64),
            IsTrusted = true,
            IsBlocked = false
        };
        trustedOrigin.GenerateIntegrityHash();
        var devices = new FakeDeviceRepository();
        devices.Seed(trustedOrigin);
        var stored = UserControlOperationMapping.ToStoredOperation(
            envelope,
            UserControlOperationEnvelopeUtil.Serialize(envelope),
            UserControlOperationStatus.StoredPending,
            DateTimeOffset.UtcNow,
            null,
            Guid.NewGuid());
        var userDevices = new FakeUserDeviceRepository();
        var localUsers = new FakeLocalUserDeviceRepository();
        var builder = new OutgoingDeltaBuilderService(
            new InMemoryUserRepository(),
            new FakeGroupRepository(),
            devices,
            userDevices,
            new FakeSyncRouteRepository(userDevices, localUsers),
            relay,
            new FakeUserSnapshotPublisherService(relay),
            new FakeUserMembershipAuthorizationService());

        var delta = await builder.BuildUserControlOperationRelayAsync(stored, target);

        MSTestAssert.AreEqual(relay.DeviceIdHex, delta.DeviceId);
        MSTestAssert.AreEqual(envelope.OperationId, delta.ControlOperationId);
        MSTestAssert.AreEqual(originDeviceId, delta.ControlOperationOriginDeviceId);
        MSTestAssert.AreEqual(originInstanceId, delta.ControlOperationOriginInstanceId);
        MSTestAssert.AreEqual(envelope.OriginSequence, delta.ControlOperationOriginSequence);
        CollectionAssert.AreEqual(envelope.OperationHash, delta.ControlOperationHash);
        MSTestAssert.IsTrue(NetDeltaSigner.VerifySignature(delta));

        var plaintext = recipient.DecryptFromDevice(
            delta.Payload,
            delta.EphemeralPublicKey,
            delta.Nonce,
            delta.Tag,
            SyncCryptoUtil.BuildAssociatedData(delta));
        var payload = JsonSerializer.Deserialize(
            plaintext,
            BackendJsonSerializerContext.Default.SyncDeltaPayload);
        MSTestAssert.IsNotNull(payload?.UserControlOperation);
        var relayed = payload!.UserControlOperation!;
        MSTestAssert.AreEqual(envelope.OperationId, relayed.OperationId);
        MSTestAssert.AreEqual(envelope.OriginDeviceId, relayed.OriginDeviceId);
        MSTestAssert.AreEqual(envelope.OriginInstanceId, relayed.OriginInstanceId);
        MSTestAssert.AreEqual(envelope.OriginSequence, relayed.OriginSequence);
        CollectionAssert.AreEqual(envelope.OperationHash, relayed.OperationHash);
        CollectionAssert.AreEqual(envelope.OriginSignature, relayed.OriginSignature);
        CollectionAssert.AreEqual(envelope.OriginSignPublicKey, relayed.OriginSignPublicKey);
        UserControlOperationEnvelopeUtil.VerifyWithSigningKey(relayed, trustedOrigin.SignPublicKey);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task Build_RejectsBlockedUntrustedAndLocalTargets()
    {
        using var senderProvider = CreateIdentityProvider();
        using var recipientProvider = CreateIdentityProvider();
        var sender = CreateIdentity(senderProvider);
        var recipient = CreateIdentity(recipientProvider);
        await sender.InitializeAsync();
        await sender.SetSyncOnAsync(true);
        await recipient.InitializeAsync();
        var userDevices = new FakeUserDeviceRepository();
        var localUsers = new FakeLocalUserDeviceRepository();
        var builder = new OutgoingDeltaBuilderService(
            new InMemoryUserRepository(),
            new FakeGroupRepository(),
            new FakeDeviceRepository(),
            userDevices,
            new FakeSyncRouteRepository(userDevices, localUsers),
            sender,
            new FakeUserSnapshotPublisherService(sender),
            new FakeUserMembershipAuthorizationService());
        var item = new SyncItem
        {
            ModelId = Guid.NewGuid(),
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Deleted
        };
        var blocked = CreateTargetDevice(recipient);
        blocked.IsBlocked = true;
        var untrusted = CreateTargetDevice(recipient);
        untrusted.IsTrusted = false;
        var local = new Device
        {
            Id = sender.LocalDeviceId,
            PublicKey = sender.AgreementPublicKey,
            SignPublicKey = sender.SignPublicKey,
            TlsCertFingerprint = sender.FingerprintHex,
            IsTrusted = true
        };

        await ExpectThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(item, blocked));
        await ExpectThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(item, untrusted));
        await ExpectThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(item, local));
    }




    private static User CreateControlReplacementUser()
    {
        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            UId = Guid.NewGuid(),
            UsernameHash = Enumerable.Repeat((byte)0x11, 32).ToArray(),
            UsernameSalt = Enumerable.Repeat((byte)0x12, 32).ToArray(),
            PasswordSalt = [0x13],
            EncryptedPayload = [0x14],
            EncryptedGeneralUserDataPayload = [0x15],
            EncryptedUserPasswordsDataPayload = [0x16],
            EncryptedUserDevicesDataPayload = [0x17],
            KeyEpoch = 2,
            MembershipEpoch = 1,
            GeneralDataVersionPhysicalTimeUnixMilliseconds = 2_000,
            GeneralDataVersionLogicalCounter = 0,
            GeneralDataVersionOriginDeviceId = Guid.Parse("02D0E935-9300-42EF-8529-4B1F3513C384"),
            GeneralDataVersionOriginInstanceId = Guid.Parse("6D29FD21-9137-45CD-8574-FEA81252BC31"),
            LastModifiedAt = now,
            UserDataLastModifiedAt = now,
            GeneralUserDataLastModifiedAt = now,
            UserPasswordsDataLastModifiedAt = now,
            UserDevicesDataLastModifiedAt = now
        };
        user.GenerateIntegrityHash();
        return user;
    }

    private static UserSnapshotEnvelope CreateSignedForeignEnvelope(Guid originDeviceId, Guid originInstanceId, Key signingKey)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var user = new UserSyncPayload
        {
            UId = userId,
            UsernameHash = Enumerable.Repeat((byte)0x01, 32).ToArray(),
            UsernameSalt = Enumerable.Repeat((byte)0x02, 32).ToArray(),
            GeneralUserDataVersion = new()
            {
                PhysicalTimeUnixMilliseconds = 1_000,
                LogicalCounter = 0,
                OriginDeviceId = originDeviceId,
                OriginInstanceId = originInstanceId
            },
            PasswordSalt = [0x03],
            EncryptedPayload = [0x04],
            EncryptedGeneralUserDataPayload = [0x05],
            EncryptedUserPasswordsDataPayload = [0x06],
            EncryptedUserDevicesDataPayload = [0x07],
            UserDataLastModifiedAt = createdAt,
            GeneralUserDataLastModifiedAt = createdAt,
            UserPasswordsDataLastModifiedAt = createdAt,
            UserDevicesDataLastModifiedAt = createdAt,
            DeviceIds = [originDeviceId]
        };
        user.IntegrityHash = SyncCryptoUtil.CalculateUserHash(user, createdAt.ToUnixTimeMilliseconds());
        var envelope = new UserSnapshotEnvelope
        {
            UserId = userId,
            OriginDeviceId = originDeviceId,
            OriginInstanceId = originInstanceId,
            OriginRevision = 15,
            UserKeyEpoch = 1,
            MembershipEpoch = 1,
            CreatedAtUtc = createdAt,
            User = user
        };
        UserSnapshotEnvelopeUtil.FillOriginAuthentication(envelope, new FakeDeviceIdentityService
        {
            LocalDeviceId = originDeviceId,
            OriginInstanceId = originInstanceId,
            SignPublicKey = signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            SignHandler = data => SignatureAlgorithm.Ed25519.Sign(signingKey, data)
        });
        return envelope;
    }

    private static void AddSourceLink(Device sourceDevice, Guid userId, bool isSyncOn)
    {
        var link = new UserDevice
        {
            UserId = userId,
            DeviceId = sourceDevice.Id,
            IsSyncOn = isSyncOn,
            IsDeleted = false
        };
        link.GenerateIntegrityHash();
        sourceDevice.UserDevices.Add(link);
    }

    private static UserDevice CreateTargetLink(Guid userId, Guid targetDeviceId, bool isSyncOn)
    {
        var link = new UserDevice
        {
            UserId = userId,
            DeviceId = targetDeviceId,
            IsSyncOn = isSyncOn,
            IsDeleted = false
        };
        link.GenerateIntegrityHash();
        return link;
    }

    private static ServiceProvider CreateIdentityProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDeviceIdentityRepository, FakeDeviceIdentityRepository>();
        services.AddSingleton<IUnitOfWork, FakeUnitOfWork>();
        services.AddSingleton<IKeyProtector, TestKeyProtector>();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    private static DeviceIdentityService CreateIdentity(IServiceProvider provider) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            () => DeviceType.WindowsPc);

    private static Device CreateTargetDevice(DeviceIdentityService recipient) =>
        new()
        {
            Id = recipient.LocalDeviceId,
            PublicKey = recipient.AgreementPublicKey,
            SignPublicKey = recipient.SignPublicKey,
            TlsCertFingerprint = recipient.FingerprintHex,
            DeviceType = recipient.DeviceType,
            IsTrusted = true,
            IsBlocked = false
        };

    private static async Task ExpectThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
            MSTestAssert.Fail($"Expected exception: {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }
}
