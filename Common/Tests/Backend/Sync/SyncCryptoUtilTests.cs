using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Sync;

[TestClass]
public sealed class SyncCryptoUtilTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void ValidateEncryptedEnvelope_AcceptsCompleteEnvelopeForLocalDevice()
    {
        var localDeviceId = Guid.NewGuid();
        var delta = CreateValidEnvelope(localDeviceId);

        Exception? validationException = null;
        try
        {
            SyncCryptoUtil.ValidateEncryptedEnvelope(delta, localDeviceId);
        }
        catch (Exception ex)
        {
            validationException = ex;
        }

        MSTestAssert.IsNull(validationException);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void ValidateEncryptedEnvelope_RejectsWrongRecipientBeforeDecryption()
    {
        var delta = CreateValidEnvelope(Guid.NewGuid());

        ExpectThrows<UnauthorizedAccessException>(() =>
            SyncCryptoUtil.ValidateEncryptedEnvelope(delta, Guid.NewGuid()));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void ValidateEncryptedEnvelope_RejectsFutureTimestampAndUnsupportedVersion()
    {
        var localDeviceId = Guid.NewGuid();
        var future = CreateValidEnvelope(localDeviceId);
        future.Ts = DateTimeOffset.UtcNow
            .AddSeconds(SyncConstants.MaxIncomingDeltaFutureSeconds + 10)
            .ToUnixTimeMilliseconds();
        ExpectThrows<InvalidDataException>(() => SyncCryptoUtil.ValidateEncryptedEnvelope(future, localDeviceId));

        var unsupported = CreateValidEnvelope(localDeviceId);
        unsupported.EncryptionVersion = SyncConstants.SyncDeltaEncryptionVersion + 1;
        ExpectThrows<InvalidDataException>(() => SyncCryptoUtil.ValidateEncryptedEnvelope(unsupported, localDeviceId));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void ValidateEncryptedEnvelope_RejectsInvalidCryptographicFieldSizes()
    {
        var localDeviceId = Guid.NewGuid();

        var invalidPublicKey = CreateValidEnvelope(localDeviceId);
        invalidPublicKey.EphemeralPublicKey = new byte[SyncConstants.SyncDeltaX25519PublicKeyBytes - 1];
        ExpectThrows<InvalidDataException>(() => SyncCryptoUtil.ValidateEncryptedEnvelope(invalidPublicKey, localDeviceId));

        var invalidNonce = CreateValidEnvelope(localDeviceId);
        invalidNonce.Nonce = new byte[SyncConstants.SyncDeltaNonceBytes - 1];
        ExpectThrows<InvalidDataException>(() => SyncCryptoUtil.ValidateEncryptedEnvelope(invalidNonce, localDeviceId));

        var invalidTag = CreateValidEnvelope(localDeviceId);
        invalidTag.Tag = new byte[SyncConstants.SyncDeltaTagBytes - 1];
        ExpectThrows<InvalidDataException>(() => SyncCryptoUtil.ValidateEncryptedEnvelope(invalidTag, localDeviceId));

        var invalidHash = CreateValidEnvelope(localDeviceId);
        invalidHash.PayloadHash = new byte[SyncConstants.SyncDeltaPayloadHashBytes - 1];
        ExpectThrows<InvalidDataException>(() => SyncCryptoUtil.ValidateEncryptedEnvelope(invalidHash, localDeviceId));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void BuildAssociatedData_BindsRecipientTimestampAndPayloadHash()
    {
        var localDeviceId = Guid.NewGuid();
        var original = CreateValidEnvelope(localDeviceId);
        var originalAad = SyncCryptoUtil.BuildAssociatedData(original);

        var changedRecipient = Clone(original);
        changedRecipient.RecipientDeviceId = Guid.NewGuid().ToString("N");
        MSTestAssert.IsFalse(originalAad.SequenceEqual(SyncCryptoUtil.BuildAssociatedData(changedRecipient)));

        var changedTimestamp = Clone(original);
        changedTimestamp.Ts++;
        MSTestAssert.IsFalse(originalAad.SequenceEqual(SyncCryptoUtil.BuildAssociatedData(changedTimestamp)));

        var changedHash = Clone(original);
        changedHash.PayloadHash[0] ^= 0x01;
        MSTestAssert.IsFalse(originalAad.SequenceEqual(SyncCryptoUtil.BuildAssociatedData(changedHash)));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void ValidatePlaintextHash_AcceptsExactPayloadAndRejectsTampering()
    {
        var plaintext = "authenticated plaintext"u8.ToArray();
        var expectedHash = Hashing.SHA256Hash(plaintext);

        SyncCryptoUtil.ValidatePlaintextHash(plaintext, expectedHash);

        plaintext[0] ^= 0x01;
        ExpectThrows<InvalidDataException>(() => SyncCryptoUtil.ValidatePlaintextHash(plaintext, expectedHash));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void ValidatePayloadShape_RejectsMissingMismatchedAndExtraPayloads()
    {
        ExpectThrows<InvalidDataException>(() => SyncCryptoUtil.ValidatePayloadShape(new SyncDeltaPayload
        {
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated
        }));

        ExpectThrows<InvalidDataException>(() => SyncCryptoUtil.ValidatePayloadShape(new SyncDeltaPayload
        {
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated,
            Group = new GroupSyncPayload()
        }));

        ExpectThrows<InvalidDataException>(() => SyncCryptoUtil.ValidatePayloadShape(new SyncDeltaPayload
        {
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Deleted,
            Group = new GroupSyncPayload()
        }));

        ExpectThrows<InvalidDataException>(() => SyncCryptoUtil.ValidatePayloadShape(new SyncDeltaPayload
        {
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Deleted
        }));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void CalculateUserHash_CanonicalizesRelationshipOrderDuplicatesAndEmptyIds()
    {
        var firstGroup = Guid.Parse("EB4A1964-2AF6-41C8-8A69-9BBFC07CF78E");
        var secondGroup = Guid.Parse("45F7B740-0883-4996-83FD-7C1588142501");
        var firstDevice = Guid.Parse("7247AE19-FE13-44EA-9EA1-709EF8186B92");
        var secondDevice = Guid.Parse("71DA6738-120B-4030-A32D-FD044DE4B704");
        var timestamp = 1_750_000_000_000L;

        var first = CreateUserPayload();
        first.GroupIds = [firstGroup, secondGroup, firstGroup, Guid.Empty];
        first.DeviceIds = [secondDevice, firstDevice, secondDevice, Guid.Empty];

        var second = CreateUserPayload();
        second.GroupIds = [secondGroup, firstGroup];
        second.DeviceIds = [firstDevice, secondDevice];

        CollectionAssert.AreEqual(
            SyncCryptoUtil.CalculateUserHash(first, timestamp),
            SyncCryptoUtil.CalculateUserHash(second, timestamp));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public void ValidatePayloadIntegrity_RejectsTamperedUserPayload()
    {
        const long timestamp = 1_750_000_000_000L;
        var user = CreateUserPayload();
        user.IntegrityHash = SyncCryptoUtil.CalculateUserHash(user, timestamp);
        var envelope = new UserSnapshotEnvelope
        {
            UserId = user.UId,
            OriginDeviceId = Guid.NewGuid(),
            OriginInstanceId = Guid.NewGuid(),
            OriginRevision = 1,
            UserKeyEpoch = 1,
            MembershipEpoch = 1,
            CreatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(timestamp),
            User = user,
            OriginSignPublicKey = new byte[SyncConstants.SyncDeltaEd25519PublicKeyBytes],
            OriginSignature = new byte[SyncConstants.SyncDeltaEd25519SignatureBytes]
        };
        envelope.SnapshotHash = UserSnapshotEnvelopeUtil.CalculateSnapshotHash(envelope);
        var payload = new SyncDeltaPayload
        {
            ModelId = user.UId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated,
            UserSnapshot = envelope
        };

        SyncCryptoUtil.ValidatePayloadIntegrity(payload, timestamp);

        user.EncryptedPayload[0] ^= 0x01;
        ExpectThrows<InvalidDataException>(() => SyncCryptoUtil.ValidatePayloadIntegrity(payload, timestamp));
    }

    private static NetworkDelta CreateValidEnvelope(Guid localDeviceId) =>
        new()
        {
            Entity = "User",
            Payload = [1, 2, 3],
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DeviceId = Guid.NewGuid().ToString("N"),
            SignPub = new byte[SyncConstants.SyncDeltaEd25519PublicKeyBytes],
            Sig = new byte[SyncConstants.SyncDeltaEd25519SignatureBytes],
            RecipientDeviceId = localDeviceId.ToString("N"),
            EncryptionVersion = SyncConstants.SyncDeltaEncryptionVersion,
            EphemeralPublicKey = new byte[SyncConstants.SyncDeltaX25519PublicKeyBytes],
            Nonce = new byte[SyncConstants.SyncDeltaNonceBytes],
            Tag = new byte[SyncConstants.SyncDeltaTagBytes],
            PayloadHash = new byte[SyncConstants.SyncDeltaPayloadHashBytes]
        };

    private static NetworkDelta Clone(NetworkDelta source) =>
        new()
        {
            Entity = source.Entity,
            Payload = source.Payload.ToArray(),
            Ts = source.Ts,
            DeviceId = source.DeviceId,
            SignPub = source.SignPub.ToArray(),
            Sig = source.Sig.ToArray(),
            RecipientDeviceId = source.RecipientDeviceId,
            EncryptionVersion = source.EncryptionVersion,
            EphemeralPublicKey = source.EphemeralPublicKey.ToArray(),
            Nonce = source.Nonce.ToArray(),
            Tag = source.Tag.ToArray(),
            PayloadHash = source.PayloadHash.ToArray()
        };

    private static UserSyncPayload CreateUserPayload() =>
        new()
        {
            UId = Guid.Parse("9FC68202-3041-45C5-80B2-2C82CF4662C8"),
            UsernameHash = Enumerable.Repeat((byte)0x11, CryptographyConstants.Sha256HashSizeInBytes).ToArray(),
            UsernameSalt = Enumerable.Repeat((byte)0x22, CryptographyConstants.Sha256HashSizeInBytes).ToArray(),
            GeneralUserDataVersion = new SyncVersionStamp
            {
                PhysicalTimeUnixMilliseconds = 1_750_000_000_000L,
                LogicalCounter = 0,
                OriginDeviceId = Guid.Parse("12E1F0B2-529C-40D2-9EE4-A17AA70682F0"),
                OriginInstanceId = Guid.Parse("3AB48C66-E0B7-4F34-B83B-7A8CDE70D5D9")
            },
            PasswordSalt = [7, 8, 9],
            EncryptedPayload = [10, 11, 12]
        };

    private static void ExpectThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            MSTestAssert.Fail($"Expected exception: {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }
}
