using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Sync;

[TestClass]
public sealed class UserSnapshotEnvelopeUtilTests
{
    [TestMethod]
    public void Verify_UnmodifiedEnvelope_AcceptsTrustedOriginalOrigin()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var deviceId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var envelope = CreateEnvelope(deviceId, instanceId, key);
        var trustedDevice = CreateTrustedDevice(deviceId, key);

        UserSnapshotEnvelopeUtil.VerifyWithSigningKey(envelope, trustedDevice.SignPublicKey);
    }

    [TestMethod]
    public void Verify_RelayModifiesImmutablePayload_RejectsEnvelope()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var deviceId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var envelope = CreateEnvelope(deviceId, instanceId, key);
        var trustedDevice = CreateTrustedDevice(deviceId, key);
        envelope.User.EncryptedUserPasswordsDataPayload[0] ^= 0x7F;

        MSTestAssert.ThrowsExactly<InvalidDataException>(() =>
            UserSnapshotEnvelopeUtil.VerifyWithSigningKey(envelope, trustedDevice.SignPublicKey));
    }


    [TestMethod]
    public void Verify_UsernameVersionChangedAfterSigning_RejectsEnvelope()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var deviceId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var envelope = CreateEnvelope(deviceId, instanceId, key);
        envelope.User.GeneralUserDataVersion = new()
        {
            PhysicalTimeUnixMilliseconds = 2_000,
            LogicalCounter = 0,
            OriginDeviceId = deviceId,
            OriginInstanceId = instanceId
        };
        envelope.User.IntegrityHash = SyncCryptoUtil.CalculateUserHash(
            envelope.User,
            envelope.CreatedAtUtc.ToUnixTimeMilliseconds());

        MSTestAssert.ThrowsExactly<InvalidDataException>(() =>
            UserSnapshotEnvelopeUtil.VerifyWithSigningKey(envelope, key.PublicKey.Export(KeyBlobFormat.RawPublicKey)));
    }

    [TestMethod]
    public void FillOriginAuthentication_MissingUsernameVersion_RejectsEnvelope()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var deviceId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var envelope = CreateUnsignedEnvelope(deviceId, instanceId);
        envelope.User.GeneralUserDataVersion = new();
        envelope.User.IntegrityHash = SyncCryptoUtil.CalculateUserHash(
            envelope.User,
            envelope.CreatedAtUtc.ToUnixTimeMilliseconds());

        MSTestAssert.ThrowsExactly<InvalidDataException>(() =>
            UserSnapshotEnvelopeUtil.FillOriginAuthentication(envelope, CreateIdentity(deviceId, instanceId, key)));
    }

    [TestMethod]
    public void FillOriginAuthentication_ForeignOriginIdentity_RejectsSigningAttempt()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var actualDeviceId = Guid.NewGuid();
        var actualInstanceId = Guid.NewGuid();
        var envelope = CreateUnsignedEnvelope(Guid.NewGuid(), Guid.NewGuid());
        var identity = CreateIdentity(actualDeviceId, actualInstanceId, key);

        MSTestAssert.ThrowsExactly<InvalidDataException>(() =>
            UserSnapshotEnvelopeUtil.FillOriginAuthentication(envelope, identity));
    }

    private static UserSnapshotEnvelope CreateEnvelope(Guid deviceId, Guid instanceId, Key key)
    {
        var envelope = CreateUnsignedEnvelope(deviceId, instanceId);
        UserSnapshotEnvelopeUtil.FillOriginAuthentication(envelope, CreateIdentity(deviceId, instanceId, key));
        return envelope;
    }

    private static UserSnapshotEnvelope CreateUnsignedEnvelope(Guid deviceId, Guid instanceId)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var payload = new UserSyncPayload
        {
            UId = userId,
            UsernameHash = Enumerable.Repeat((byte)0x01, 32).ToArray(),
            UsernameSalt = Enumerable.Repeat((byte)0x02, 32).ToArray(),
            GeneralUserDataVersion = new()
            {
                PhysicalTimeUnixMilliseconds = 1_000,
                LogicalCounter = 0,
                OriginDeviceId = deviceId,
                OriginInstanceId = instanceId
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
            DeviceIds = [deviceId]
        };
        payload.IntegrityHash = SyncCryptoUtil.CalculateUserHash(payload, createdAt.ToUnixTimeMilliseconds());
        return new UserSnapshotEnvelope
        {
            UserId = userId,
            OriginDeviceId = deviceId,
            OriginInstanceId = instanceId,
            OriginRevision = 1,
            UserKeyEpoch = 1,
            MembershipEpoch = 1,
            CreatedAtUtc = createdAt,
            User = payload
        };
    }

    private static FakeDeviceIdentityService CreateIdentity(Guid deviceId, Guid instanceId, Key key) =>
        new()
        {
            LocalDeviceId = deviceId,
            OriginInstanceId = instanceId,
            SignPublicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            SignHandler = data => SignatureAlgorithm.Ed25519.Sign(key, data)
        };

    private static Device CreateTrustedDevice(Guid deviceId, Key key)
    {
        var device = new Device
        {
            Id = deviceId,
            SignPublicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            PublicKey = new byte[32],
            TlsCertFingerprint = new string('A', 64),
            IsTrusted = true,
            IsBlocked = false,
            DeviceType = DeviceType.WindowsPc
        };
        device.GenerateIntegrityHash();
        return device;
    }
}
