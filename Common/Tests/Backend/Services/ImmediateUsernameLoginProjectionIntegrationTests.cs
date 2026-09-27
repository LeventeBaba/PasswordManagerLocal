using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;
namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class ImmediateUsernameLoginProjectionIntegrationTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task PendingSignedUsernameChange_IsEffectiveWhileLocked_LoginMergesAndKeepsOldUsernameInvalid()
    {
        using var host = new BackendTestHost(useRealSnapshotMergeCoordinator: true);
        var scenario = await StorePendingRenameAsync(
            host,
            oldUsername: "pending-old-username",
            advertisedUsername: "pending-new-username",
            encryptedUsername: "pending-new-username");
        var auth = host.Services.GetRequiredService<IAuthService>();
        var users = host.Services.GetRequiredService<IUserRepository>();
        var reader = host.Services.GetRequiredService<IUserDataReaderService>();
        var lookup = host.Services.GetRequiredService<IUserLookupService>();
        var snapshots = host.Services.GetRequiredService<IUserSyncSnapshotRepository>();

        MSTestAssert.AreEqual(UserSnapshotReceiptState.StoredPending, scenario.Receipt.State);
        var stillUnmerged = await users.GetByIdAsync(scenario.UserId)
            ?? throw new AssertFailedException("The canonical user disappeared.");
        CollectionAssert.AreEqual(scenario.CanonicalHashBefore, stillUnmerged.UsernameHash);
        MSTestAssert.AreEqual(scenario.CanonicalVersionBefore, stillUnmerged.GetGeneralUserDataVersion());
        MSTestAssert.IsNull(await lookup.GetUserByUsernameAsync(Encoding.UTF8.GetBytes(scenario.OldUsername)));
        MSTestAssert.AreEqual(
            scenario.UserId,
            (await lookup.GetUserByUsernameAsync(Encoding.UTF8.GetBytes(scenario.AdvertisedUsername)))?.UId);

        var loginToken = await auth.LoginAsync(host.CreateValidLoginRequest(scenario.AdvertisedUsername));
        var canonicalAfter = await users.GetByIdAsync(scenario.UserId)
            ?? throw new AssertFailedException("The merged user is missing.");
        CollectionAssert.AreEqual(scenario.AdvertisedHash, canonicalAfter.UsernameHash);
        MSTestAssert.AreEqual(scenario.IncomingVersion, canonicalAfter.GetGeneralUserDataVersion());
        MSTestAssert.IsNull(await lookup.GetUserByUsernameAsync(Encoding.UTF8.GetBytes(scenario.OldUsername)));
        MSTestAssert.AreEqual(
            scenario.UserId,
            (await lookup.GetUserByUsernameAsync(Encoding.UTF8.GetBytes(scenario.AdvertisedUsername)))?.UId);

        using var mergedBundle = await reader.GetLoadAndVerifyUserDataBundleAsync(loginToken, user: canonicalAfter);
        MSTestAssert.AreEqual(scenario.AdvertisedUsername, mergedBundle.GeneralUserData.Username);
        var retained = await snapshots.GetAsync(
            scenario.UserId,
            scenario.SourceDeviceId,
            scenario.SourceInstanceId,
            canonicalAfter.KeyEpoch);
        MSTestAssert.IsNotNull(retained);
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.MergedReceipt, retained!.Status);
        MSTestAssert.AreEqual(scenario.RelayDeviceId, retained.LastReceivedFromDeviceId);
        MSTestAssert.AreEqual(scenario.SourceDeviceId, retained.OriginDeviceId);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task SignedProjectionThatDisagreesWithDecryptedUsername_IsQuarantinedAndCannotCompleteLogin()
    {
        using var host = new BackendTestHost(useRealSnapshotMergeCoordinator: true);
        var scenario = await StorePendingRenameAsync(
            host,
            oldUsername: "mismatch-old-username",
            advertisedUsername: "mismatch-advertised-username",
            encryptedUsername: "mismatch-encrypted-username");
        var auth = host.Services.GetRequiredService<IAuthService>();
        var users = host.Services.GetRequiredService<IUserRepository>();
        var lookup = host.Services.GetRequiredService<IUserLookupService>();
        var snapshots = host.Services.GetRequiredService<IUserSyncSnapshotRepository>();

        MSTestAssert.IsNull(await lookup.GetUserByUsernameAsync(Encoding.UTF8.GetBytes(scenario.OldUsername)));
        MSTestAssert.AreEqual(
            scenario.UserId,
            (await lookup.GetUserByUsernameAsync(Encoding.UTF8.GetBytes(scenario.AdvertisedUsername)))?.UId);

        await MSTestAssert.ThrowsExactlyAsync<UsernameChangedDuringLoginException>(() =>
            auth.LoginAsync(host.CreateValidLoginRequest(scenario.AdvertisedUsername)));

        var canonicalAfter = await users.GetByIdAsync(scenario.UserId)
            ?? throw new AssertFailedException("The canonical user disappeared after quarantine.");
        CollectionAssert.AreEqual(scenario.CanonicalHashBefore, canonicalAfter.UsernameHash);
        MSTestAssert.AreEqual(scenario.CanonicalVersionBefore, canonicalAfter.GetGeneralUserDataVersion());
        // A known newer signed username mutation exists, but its decrypted username metadata
        // could not be verified. The account must remain frozen instead of falling back to the
        // older canonical username, which could otherwise make an unsafe stale identity effective.
        MSTestAssert.IsNull(await lookup.GetUserByUsernameAsync(Encoding.UTF8.GetBytes(scenario.OldUsername)));
        MSTestAssert.IsNull(await lookup.GetUserByUsernameAsync(Encoding.UTF8.GetBytes(scenario.AdvertisedUsername)));
        var retained = await snapshots.GetAsync(
            scenario.UserId,
            scenario.SourceDeviceId,
            scenario.SourceInstanceId,
            canonicalAfter.KeyEpoch);
        MSTestAssert.IsNotNull(retained);
        MSTestAssert.AreEqual(UserSyncSnapshotStatus.IsolatedCorrupt, retained!.Status);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task EnrollmentExport_RejectsCanonicalProjectionThatDisagreesWithDecryptedUsername()
    {
        using var host = new BackendTestHost(useRealSnapshotMergeCoordinator: true);
        var auth = host.Services.GetRequiredService<IAuthService>();
        var sessions = host.Services.GetRequiredService<IUserSessionService>();
        var users = host.Services.GetRequiredService<IUserRepository>();
        var unitOfWork = host.Services.GetRequiredService<PasswordManagerLocal.Common.Backend.Abstractions.Persistence.IUnitOfWork>();
        var identity = host.Services.GetRequiredService<IDeviceIdentityService>();
        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("enrollment-projection-user"));
        var userId = sessions.GetUidFromToken(token);
        var user = await users.GetByIdAsync(userId)
            ?? throw new AssertFailedException("The registered user is missing.");
        var inconsistentUsername = Encoding.UTF8.GetBytes("different-enrollment-username");
        try
        {
            CryptographicOperations.ZeroMemory(user.UsernameHash);
            CryptographicOperations.ZeroMemory(user.UsernameSalt);
            user.UsernameSalt = Hashing.GenerateSalt();
            user.UsernameHash = Hashing.SHA256Hash(inconsistentUsername, user.UsernameSalt);
            user.GenerateIntegrityHash();
            users.Update(user);
            await unitOfWork.SaveChangesAsync();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inconsistentUsername);
        }

        var snapshotService = new DeviceEnrollmentSnapshotService(identity);
        await MSTestAssert.ThrowsExactlyAsync<InvalidDataException>(() =>
            snapshotService.BuildAsync(
                host.Services,
                userId,
                new EnrollmentEndpoint(),
                Guid.NewGuid()));
    }

    private static async Task<PendingRenameScenario> StorePendingRenameAsync(
        BackendTestHost host,
        string oldUsername,
        string advertisedUsername,
        string encryptedUsername)
    {
        var auth = host.Services.GetRequiredService<IAuthService>();
        var sessions = host.Services.GetRequiredService<IUserSessionService>();
        var users = host.Services.GetRequiredService<IUserRepository>();
        var reader = host.Services.GetRequiredService<IUserDataReaderService>();
        var inbox = host.Services.GetRequiredService<IUserSnapshotInboxService>();
        var authorizations = host.Services.GetRequiredService<IUserMembershipAuthorizationRepository>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest(oldUsername));
        var userId = sessions.GetUidFromToken(token);
        var canonicalBefore = await users.GetByIdAsync(userId)
            ?? throw new AssertFailedException("The registered user is missing.");
        var canonicalHashBefore = canonicalBefore.UsernameHash.ToArray();
        var canonicalVersionBefore = canonicalBefore.GetGeneralUserDataVersion();
        using var bundle = await reader.GetLoadAndVerifyUserDataBundleAsync(token, user: canonicalBefore);

        using var sourceSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var sourceDeviceId = Guid.NewGuid();
        var sourceInstanceId = Guid.NewGuid();
        var sourceIdentity = new FakeDeviceIdentityService
        {
            LocalDeviceId = sourceDeviceId,
            OriginInstanceId = sourceInstanceId,
            SignPublicKey = sourceSigningKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            AgreementPublicKey = Enumerable.Repeat((byte)0x44, 32).ToArray(),
            FingerprintHex = new string('D', 64),
            DeviceType = DeviceType.WindowsPc,
            SignHandler = bytes => SignatureAlgorithm.Ed25519.Sign(sourceSigningKey, bytes)
        };
        await authorizations.AddAsync(new UserMembershipAuthorization
        {
            AuthorizationId = Guid.NewGuid(),
            UserId = userId,
            DeviceId = sourceDeviceId,
            OriginInstanceId = sourceInstanceId,
            SignPublicKey = sourceIdentity.SignPublicKey.ToArray(),
            SignPublicKeyHash = Hashing.SHA256Hash(sourceIdentity.SignPublicKey),
            AgreementPublicKeyHash = Hashing.SHA256Hash(sourceIdentity.AgreementPublicKey),
            TlsCertFingerprint = sourceIdentity.FingerprintHex,
            DeviceType = sourceIdentity.DeviceType,
            StartedMembershipEpoch = 1,
            MinimumKeyEpoch = canonicalBefore.KeyEpoch,
            IsActive = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        using var incomingGeneral = Clone(bundle.GeneralUserData);
        incomingGeneral.Username = encryptedUsername;
        incomingGeneral.LastUpdatedAt = DateTime.UtcNow;
        incomingGeneral.Version = new SyncVersionStamp
        {
            PhysicalTimeUnixMilliseconds = checked(canonicalVersionBefore.PhysicalTimeUnixMilliseconds + 1),
            LogicalCounter = 0,
            OriginDeviceId = sourceDeviceId,
            OriginInstanceId = sourceInstanceId
        };
        incomingGeneral.GenerateIntegrityHash();

        var advertisedUsernameBytes = Encoding.UTF8.GetBytes(advertisedUsername);
        var advertisedSalt = Hashing.GenerateSalt();
        var advertisedHash = Hashing.SHA256Hash(advertisedUsernameBytes, advertisedSalt);
        byte[] encryptedGeneral;
        using (var generalKey = EncryptionKey.FromRaw(bundle.UserData.GeneralUserDataKey))
        {
            encryptedGeneral = await DataCodec.SerializeCompressEncryptAsync(
                incomingGeneral,
                generalKey,
                BackendJsonSerializerContext.Default.GeneralUserData);
        }

        using var incomingUserData = Clone(bundle.UserData);
        CryptographicOperations.ZeroMemory(incomingUserData.GeneralUserDataIntegrityHash);
        incomingUserData.GeneralUserDataIntegrityHash = incomingGeneral.IntegrityHash.ToArray();
        incomingUserData.GenerateIntegrityHash();

        byte[] encryptedUserData;
        using (var userKey = sessions.GetEncryptionKeyFromToken(token))
        {
            encryptedUserData = await DataCodec.SerializeCompressEncryptAsync(
                incomingUserData,
                userKey,
                BackendJsonSerializerContext.Default.UserData);
        }

        var createdAt = DateTimeOffset.UtcNow;
        var payload = new UserSyncPayload
        {
            UId = userId,
            UsernameHash = advertisedHash.ToArray(),
            UsernameSalt = advertisedSalt.ToArray(),
            GeneralUserDataVersion = incomingGeneral.Version,
            PasswordSalt = canonicalBefore.PasswordSalt.ToArray(),
            EncryptedPayload = encryptedUserData,
            EncryptedGeneralUserDataPayload = encryptedGeneral,
            EncryptedUserPasswordsDataPayload = canonicalBefore.EncryptedUserPasswordsDataPayload.ToArray(),
            EncryptedUserDevicesDataPayload = canonicalBefore.EncryptedUserDevicesDataPayload.ToArray(),
            UserDataLastModifiedAt = createdAt,
            GeneralUserDataLastModifiedAt = createdAt,
            UserPasswordsDataLastModifiedAt = canonicalBefore.UserPasswordsDataLastModifiedAt,
            UserDevicesDataLastModifiedAt = canonicalBefore.UserDevicesDataLastModifiedAt,
            DeviceIds = [sourceDeviceId]
        };
        payload.IntegrityHash = SyncCryptoUtil.CalculateUserHash(payload, createdAt.ToUnixTimeMilliseconds());
        var envelope = new UserSnapshotEnvelope
        {
            UserId = userId,
            OriginDeviceId = sourceDeviceId,
            OriginInstanceId = sourceInstanceId,
            OriginRevision = 1,
            UserKeyEpoch = canonicalBefore.KeyEpoch,
            MembershipEpoch = canonicalBefore.MembershipEpoch,
            CreatedAtUtc = createdAt,
            User = payload
        };
        UserSnapshotEnvelopeUtil.FillOriginAuthentication(envelope, sourceIdentity);

        auth.Logout(token);
        var relayDeviceId = Guid.NewGuid();
        var receipt = await inbox.StoreAsync(envelope, relayDeviceId);
        CryptographicOperations.ZeroMemory(advertisedUsernameBytes);
        CryptographicOperations.ZeroMemory(advertisedSalt);

        return new PendingRenameScenario(
            userId,
            oldUsername,
            advertisedUsername,
            canonicalHashBefore,
            canonicalVersionBefore,
            advertisedHash,
            incomingGeneral.Version,
            sourceDeviceId,
            sourceInstanceId,
            relayDeviceId,
            receipt);
    }

    private static GeneralUserData Clone(GeneralUserData source)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(source, BackendJsonSerializerContext.Default.GeneralUserData);
        return JsonSerializer.Deserialize(bytes, BackendJsonSerializerContext.Default.GeneralUserData)
            ?? throw new AssertFailedException("General user data clone failed.");
    }

    private static UserData Clone(UserData source)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(source, BackendJsonSerializerContext.Default.UserData);
        return JsonSerializer.Deserialize(bytes, BackendJsonSerializerContext.Default.UserData)
            ?? throw new AssertFailedException("User data clone failed.");
    }

}
