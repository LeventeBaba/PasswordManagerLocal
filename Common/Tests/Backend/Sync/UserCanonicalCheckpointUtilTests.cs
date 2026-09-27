using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Sync;

[TestClass]
public sealed class UserCanonicalCheckpointUtilTests
{
    [TestMethod]
    [TestCategory("Backend")]
    public void CreateAndVerify_SurvivesSavedKeyOnlyChange()
    {
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var identity = CreateIdentity(signingKey);
        var user = CreateUser();
        var checkpoint = UserCanonicalCheckpointUtil.Create(user, 1, DateTimeOffset.UtcNow, identity);

        user.SavedKey = [9, 8, 7, 6];
        UserCanonicalCheckpointUtil.Verify(user, checkpoint, identity);
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void Verify_SelfConsistentEncryptedBlobMutationFailsCheckpoint()
    {
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var identity = CreateIdentity(signingKey);
        var user = CreateUser();
        var checkpoint = UserCanonicalCheckpointUtil.Create(user, 1, DateTimeOffset.UtcNow, identity);

        user.EncryptedGeneralUserDataPayload[0] ^= 0x5A;
        user.GenerateIntegrityHash();

        MSTestAssert.Throws<InvalidDataException>(() =>
            UserCanonicalCheckpointUtil.Verify(user, checkpoint, identity));
    }

    [TestMethod]
    [TestCategory("Backend")]
    public void Verify_SignatureMutationAndDifferentLocalOriginFailClosed()
    {
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var identity = CreateIdentity(signingKey);
        var user = CreateUser();
        var checkpoint = UserCanonicalCheckpointUtil.Create(user, 1, DateTimeOffset.UtcNow, identity);

        checkpoint.Signature[0] ^= 0x01;
        MSTestAssert.Throws<InvalidDataException>(() =>
            UserCanonicalCheckpointUtil.Verify(user, checkpoint, identity));

        checkpoint = UserCanonicalCheckpointUtil.Create(user, 2, DateTimeOffset.UtcNow, identity);
        var otherIdentity = CreateIdentity(signingKey);
        otherIdentity.OriginInstanceId = Guid.NewGuid();
        MSTestAssert.Throws<InvalidDataException>(() =>
            UserCanonicalCheckpointUtil.Verify(user, checkpoint, otherIdentity));
    }

    private static FakeDeviceIdentityService CreateIdentity(Key signingKey) =>
        new()
        {
            LocalDeviceId = Guid.NewGuid(),
            OriginInstanceId = Guid.NewGuid(),
            SignPublicKey = signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            SignHandler = data => SignatureAlgorithm.Ed25519.Sign(signingKey, data)
        };

    private static User CreateUser()
    {
        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            UId = Guid.NewGuid(),
            UsernameHash = Enumerable.Repeat((byte)0x11, 32).ToArray(),
            UsernameSalt = Enumerable.Repeat((byte)0x12, 32).ToArray(),
            PasswordSalt = Enumerable.Repeat((byte)0x13, 32).ToArray(),
            EncryptedPayload = [1, 2, 3, 4],
            EncryptedGeneralUserDataPayload = [5, 6, 7, 8],
            EncryptedUserPasswordsDataPayload = [9, 10, 11, 12],
            EncryptedUserDevicesDataPayload = [13, 14, 15, 16],
            KeyEpoch = 1,
            MembershipEpoch = 1,
            LastModifiedAt = now,
            UserDataLastModifiedAt = now,
            GeneralUserDataLastModifiedAt = now,
            UserPasswordsDataLastModifiedAt = now,
            UserDevicesDataLastModifiedAt = now
        };
        user.SetGeneralUserDataVersion(new SyncVersionStamp
        {
            PhysicalTimeUnixMilliseconds = now.ToUnixTimeMilliseconds(),
            LogicalCounter = 1,
            OriginDeviceId = Guid.NewGuid(),
            OriginInstanceId = Guid.NewGuid()
        });
        user.GenerateIntegrityHash();
        return user;
    }
}
