using PasswordManagerLocal.Common.Backend.Constants;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Text;
using static PasswordManagerLocal.Common.Backend.Utils.DataCodec;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;
namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserDataBundleVerificationServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public async Task VerifyCanonicalAsync_ValidBundle_ReturnsHealthy()
    {
        var fixture = await CreateFixtureAsync();
        using (fixture.Key)
        using (var result = await CreateService().VerifyCanonicalAsync(
                   fixture.User,
                   fixture.Key,
                   UserSyncKeyConfidence.ExplicitlyTrusted))
        {
            MSTestAssert.IsTrue(result.IsHealthy);
            MSTestAssert.AreEqual(UserDataVerificationState.Healthy, result.State);
            MSTestAssert.AreEqual(UserDataBlobKind.None, result.FailedBlobs);
        }
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public async Task VerifyCanonicalAsync_RootCiphertextMutation_ReportsRootDecryptFailure()
    {
        await AssertCiphertextMutationAsync(
            user => user.EncryptedPayload[0] ^= 0x5A,
            UserDataVerificationState.RootDecryptFailure,
            UserDataBlobKind.All);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public async Task VerifyCanonicalAsync_GeneralCiphertextMutation_ReportsGeneralBlobFailure()
    {
        await AssertCiphertextMutationAsync(
            user => user.EncryptedGeneralUserDataPayload[0] ^= 0x5A,
            UserDataVerificationState.GeneralBlobFailure,
            UserDataBlobKind.General);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public async Task VerifyCanonicalAsync_PasswordsCiphertextMutation_ReportsPasswordsBlobFailure()
    {
        await AssertCiphertextMutationAsync(
            user => user.EncryptedUserPasswordsDataPayload[0] ^= 0x5A,
            UserDataVerificationState.PasswordsBlobFailure,
            UserDataBlobKind.Passwords);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public async Task VerifyCanonicalAsync_DevicesCiphertextMutation_ReportsDevicesBlobFailure()
    {
        await AssertCiphertextMutationAsync(
            user => user.EncryptedUserDevicesDataPayload[0] ^= 0x5A,
            UserDataVerificationState.DevicesBlobFailure,
            UserDataBlobKind.Devices);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public async Task VerifyCanonicalAsync_MultipleChildFailures_ReportsFirstFailureInStableOrder()
    {
        await AssertCiphertextMutationAsync(
            user =>
            {
                user.EncryptedGeneralUserDataPayload[0] ^= 0x5A;
                user.EncryptedUserPasswordsDataPayload[0] ^= 0x5A;
                user.EncryptedUserDevicesDataPayload[0] ^= 0x5A;
            },
            UserDataVerificationState.GeneralBlobFailure,
            UserDataBlobKind.General);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public async Task VerifyCanonicalAsync_ValidRootWithMismatchedChildCommitment_ReportsBundleLinkFailure()
    {
        var fixture = await CreateFixtureAsync(bundle =>
        {
            bundle.UserData.GeneralUserDataIntegrityHash[0] ^= 0x5A;
            bundle.UserData.GenerateIntegrityHash();
        });
        using (fixture.Key)
        using (var result = await CreateService().VerifyCanonicalAsync(
                   fixture.User,
                   fixture.Key,
                   UserSyncKeyConfidence.ExplicitlyTrusted))
        {
            MSTestAssert.AreEqual(UserDataVerificationState.BundleLinkFailure, result.State);
            MSTestAssert.AreEqual(UserDataBlobKind.All, result.FailedBlobs);
        }
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Security")]
    public async Task VerifyCanonicalAsync_UsernameMetadataMismatch_ReportsLoginMetadataFailure()
    {
        var fixture = await CreateFixtureAsync();
        fixture.User.UsernameHash = Enumerable.Repeat((byte)0xA5, CryptographyConstants.Sha256HashSizeInBytes).ToArray();
        fixture.User.GenerateIntegrityHash();

        using (fixture.Key)
        using (var result = await CreateService().VerifyCanonicalAsync(
                   fixture.User,
                   fixture.Key,
                   UserSyncKeyConfidence.ExplicitlyTrusted))
        {
            MSTestAssert.AreEqual(UserDataVerificationState.LoginMetadataFailure, result.State);
            MSTestAssert.AreEqual(UserDataBlobKind.General, result.FailedBlobs);
        }
    }

    private static UserDataBundleVerificationService CreateService() =>
        new(new UserDataBundleIntegrityService());

    private static async Task AssertCiphertextMutationAsync(
        Action<User> mutate,
        UserDataVerificationState expectedState,
        UserDataBlobKind expectedBlob)
    {
        var fixture = await CreateFixtureAsync();
        mutate(fixture.User);
        using (fixture.Key)
        using (var result = await CreateService().VerifyCanonicalAsync(
                   fixture.User,
                   fixture.Key,
                   UserSyncKeyConfidence.ExplicitlyTrusted))
        {
            MSTestAssert.AreEqual(expectedState, result.State);
            MSTestAssert.AreEqual(expectedBlob, result.FailedBlobs);
            MSTestAssert.IsFalse(result.IsHealthy);
        }
    }

    private static async Task<VerificationFixture> CreateFixtureAsync(Action<UserDataBundle>? mutateBeforeEncryption = null)
    {
        var integrity = new UserDataBundleIntegrityService();
        var version = new SyncVersionStamp
        {
            PhysicalTimeUnixMilliseconds = 10_000,
            LogicalCounter = 0,
            OriginDeviceId = Guid.NewGuid(),
            OriginInstanceId = Guid.NewGuid()
        };
        using var passwordKey = EncryptionKey.Create();
        using var bundle = new UserDataBundle
        {
            UserData = new UserData { UId = Guid.NewGuid() },
            GeneralUserData = new GeneralUserData
            {
                Username = "verification-user",
                Version = version,
                RegistrationDate = DateTime.UtcNow,
                RegistrationTimeZoneId = "UTC",
                RegistrationDeviceType = DeviceType.WindowsPc,
                LastUpdatedAt = DateTime.UtcNow
            },
            UserPasswordsData = new UserPasswordsData
            {
                PasswordKey = passwordKey.ExportCopy()
            },
            UserDevicesData = new UserDevicesData()
        };
        UserDataKeyUtil.InitializeUserDataKeys(bundle.UserData);
        integrity.RebuildInitialIntegrity(bundle);
        mutateBeforeEncryption?.Invoke(bundle);

        var key = EncryptionKey.Create();
        var rootTask = SerializeCompressEncryptAsync(
            bundle.UserData,
            key,
            BackendJsonSerializerContext.Default.UserData);
        using var generalKey = EncryptionKey.FromRaw(bundle.UserData.GeneralUserDataKey);
        using var passwordsKey = EncryptionKey.FromRaw(bundle.UserData.UserPasswordsDataKey);
        using var devicesKey = EncryptionKey.FromRaw(bundle.UserData.UserDevicesDataKey);
        var generalTask = SerializeCompressEncryptAsync(
            bundle.GeneralUserData,
            generalKey,
            BackendJsonSerializerContext.Default.GeneralUserData);
        var passwordsTask = SerializeCompressEncryptAsync(
            bundle.UserPasswordsData,
            passwordsKey,
            BackendJsonSerializerContext.Default.UserPasswordsData);
        var devicesTask = SerializeCompressEncryptAsync(
            bundle.UserDevicesData,
            devicesKey,
            BackendJsonSerializerContext.Default.UserDevicesData);
        await Task.WhenAll(rootTask, generalTask, passwordsTask, devicesTask);

        var usernameBytes = Encoding.UTF8.GetBytes(bundle.GeneralUserData.Username);
        var usernameSalt = Hashing.GenerateSalt();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var user = new User
            {
                UId = bundle.UserData.UId,
                UsernameSalt = usernameSalt,
                UsernameHash = Hashing.SHA256Hash(usernameBytes, usernameSalt),
                PasswordSalt = Hashing.GenerateSalt(),
                EncryptedPayload = await rootTask,
                EncryptedGeneralUserDataPayload = await generalTask,
                EncryptedUserPasswordsDataPayload = await passwordsTask,
                EncryptedUserDevicesDataPayload = await devicesTask,
                KeyEpoch = 1,
                MembershipEpoch = 1,
                LastModifiedAt = now,
                UserDataLastModifiedAt = now,
                GeneralUserDataLastModifiedAt = now,
                UserPasswordsDataLastModifiedAt = now,
                UserDevicesDataLastModifiedAt = now
            };
            user.SetGeneralUserDataVersion(version);
            user.GenerateIntegrityHash();
            return new VerificationFixture(user, key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(usernameBytes);
        }
    }

}
