using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserLoginIdentityProjectionServiceTests
{
    private static readonly Guid CanonicalDevice = Guid.Parse("01000000-0000-0000-0000-000000000001");
    private static readonly Guid CanonicalInstance = Guid.Parse("01000000-0000-0000-0000-000000000002");
    private static readonly Guid DeviceA = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid InstanceA = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid DeviceB = Guid.Parse("F0000000-0000-0000-0000-000000000001");
    private static readonly Guid InstanceB = Guid.Parse("F0000000-0000-0000-0000-000000000002");

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task ConcurrentChanges_ConvergeIndependentOfArrivalOrder_AndSurviveRestart()
    {
        var firstOrder = await ResolveConcurrentWinnerAsync(receiveBetaFirst: false);
        var reverseOrder = await ResolveConcurrentWinnerAsync(receiveBetaFirst: true);

        MSTestAssert.AreEqual("Beta", firstOrder);
        MSTestAssert.AreEqual(firstOrder, reverseOrder);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task ExactVersionWithDifferentMetadata_FailsClosed()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var service = CreateService(database);
        var canonicalVersion = Stamp(1_000, 0, CanonicalDevice, CanonicalInstance);
        var user = await AddCanonicalAsync(database, service, "Old", canonicalVersion);
        var sharedVersion = Stamp(2_000, 0, DeviceA, InstanceA);

        using var keyA = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var keyB = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        await AddSnapshotAsync(database, CreateEnvelope(user, "Alpha", sharedVersion, DeviceA, InstanceA, 1, keyA));
        await AddSnapshotAsync(database, CreateEnvelope(user, "Beta", sharedVersion, DeviceB, InstanceB, 1, keyB));

        var projection = await service.RecalculateAsync(user.UId);
        MSTestAssert.IsNotNull(projection);
        MSTestAssert.AreEqual(UserLoginIdentityStatus.IntegrityConflict, projection!.Status);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.ProjectionQuarantined,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("Old"))).State);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.InvalidProjection,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("Alpha"))).State);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.InvalidProjection,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("Beta"))).State);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task OlderUsernameEvidenceArrivingLater_DoesNotRegressEffectiveIdentity()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var service = CreateService(database);
        var user = await AddCanonicalAsync(
            database,
            service,
            "Old",
            Stamp(1_000, 0, CanonicalDevice, CanonicalInstance));
        using var newerKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var newer = CreateEnvelope(
            user,
            "Newest",
            Stamp(3_000, 0, DeviceA, InstanceA),
            DeviceA,
            InstanceA,
            1,
            newerKey);
        await AddSnapshotAsync(database, newer);
        await service.RecalculateAsync(user.UId);

        using var olderKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var older = CreateEnvelope(
            user,
            "OlderRemote",
            Stamp(2_000, 0, DeviceB, InstanceB),
            DeviceB,
            InstanceB,
            1,
            olderKey);
        await AddSnapshotAsync(database, older);
        await service.RecalculateAsync(user.UId);

        MSTestAssert.AreEqual(UserLoginIdentityMatchState.Matched,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("Newest"))).State);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.NotFound,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("OlderRemote"))).State);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.NotFound,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("Old"))).State);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task ExactVersionWithSameMetadata_IsIdempotent()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var service = CreateService(database);
        var user = await AddCanonicalAsync(
            database,
            service,
            "Old",
            Stamp(1_000, 0, CanonicalDevice, CanonicalInstance));
        var sharedVersion = Stamp(2_000, 0, DeviceA, InstanceA);
        using var keyA = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var keyB = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var first = CreateEnvelope(user, "Alpha", sharedVersion, DeviceA, InstanceA, 1, keyA);
        var equivalent = CreateEnvelope(
            user,
            "ignored",
            sharedVersion,
            DeviceB,
            InstanceB,
            1,
            keyB,
            usernameHash: first.User.UsernameHash,
            usernameSalt: first.User.UsernameSalt);

        await AddSnapshotAsync(database, first);
        await AddSnapshotAsync(database, equivalent);
        var projection = await service.RecalculateAsync(user.UId);

        MSTestAssert.IsNotNull(projection);
        MSTestAssert.AreEqual(UserLoginIdentityStatus.Active, projection!.Status);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.Matched,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("Alpha"))).State);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.NotFound,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("Old"))).State);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task ReceiptEvidence_CannotRegressOrInventUsernameState()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var service = CreateService(database);
        var canonicalVersion = Stamp(3_000, 0, CanonicalDevice, CanonicalInstance);
        var user = await AddCanonicalAsync(database, service, "Current", canonicalVersion);
        using var olderKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var older = CreateEnvelope(
            user,
            "Previous",
            Stamp(2_000, 0, DeviceA, InstanceA),
            DeviceA,
            InstanceA,
            1,
            olderKey);
        await AddSnapshotAsync(database, older, UserSyncSnapshotStatus.MergedReceipt);

        var afterOlderReceipt = await service.RecalculateAsync(user.UId);
        MSTestAssert.IsNotNull(afterOlderReceipt);
        MSTestAssert.AreEqual(UserLoginIdentityStatus.Active, afterOlderReceipt!.Status);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.Matched,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("Current"))).State);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.NotFound,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("Previous"))).State);

        using var newerKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var newer = CreateEnvelope(
            user,
            "ImpossibleReceipt",
            Stamp(4_000, 0, DeviceB, InstanceB),
            DeviceB,
            InstanceB,
            1,
            newerKey);
        await AddSnapshotAsync(database, newer, UserSyncSnapshotStatus.MergedReceipt);

        var afterNewerReceipt = await service.RecalculateAsync(user.UId);
        MSTestAssert.IsNotNull(afterNewerReceipt);
        MSTestAssert.AreEqual(UserLoginIdentityStatus.InvalidSource, afterNewerReceipt!.Status);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.InvalidProjection,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("ImpossibleReceipt"))).State);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task PendingUsernameCollision_IsAmbiguousAndLeavesUnrelatedAccountsUsable()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var service = CreateService(database);
        var renamedUser = await AddCanonicalAsync(
            database,
            service,
            "OriginalA",
            Stamp(1_000, 0, CanonicalDevice, CanonicalInstance));
        await AddCanonicalAsync(
            database,
            service,
            "Collision",
            Stamp(1_001, 0, DeviceA, InstanceA));
        await AddCanonicalAsync(
            database,
            service,
            "Unrelated",
            Stamp(1_002, 0, DeviceB, InstanceB));
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        await AddSnapshotAsync(
            database,
            CreateEnvelope(
                renamedUser,
                "Collision",
                Stamp(2_000, 0, DeviceA, InstanceA),
                DeviceA,
                InstanceA,
                1,
                key));
        await service.RecalculateAsync(renamedUser.UId);

        var collision = await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("Collision"));
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.Ambiguous, collision.State);
        MSTestAssert.IsNull(collision.UserId);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.NotFound,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("OriginalA"))).State);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.Matched,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("Unrelated"))).State);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task DuplicateEffectiveUsername_ReturnsAmbiguousWithoutSelectingAnAccount()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var service = CreateService(database);
        await AddCanonicalAsync(database, service, "Collision", Stamp(1_000, 0, CanonicalDevice, CanonicalInstance));
        await AddCanonicalAsync(database, service, "Collision", Stamp(1_001, 0, DeviceA, InstanceA));

        var result = await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("Collision"));

        MSTestAssert.AreEqual(UserLoginIdentityMatchState.Ambiguous, result.State);
        MSTestAssert.IsNull(result.UserId);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task HistoricalPreRemovalUsernameIsAccepted_PostCutoffChangeIsRejected()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var lifecycle = new UserLifecycleCoordinator();
        var identity = new FakeDeviceIdentityService
        {
            LocalDeviceId = Guid.NewGuid(),
            OriginInstanceId = Guid.NewGuid()
        };
        var membership = new UserMembershipAuthorizationService(
            database.UserMembershipAuthorizations,
            database.UserOriginRemovalCutoffs,
            identity);
        var service = new UserLoginIdentityProjectionService(
            database.Users,
            database.UserSyncSnapshots,
            membership,
            lifecycle,
            database.UnitOfWork,
            database.DeletedUserBarriers);
        var user = await AddCanonicalAsync(
            database,
            service,
            "Current",
            Stamp(1_000, 0, CanonicalDevice, CanonicalInstance));
        user.MembershipEpoch = 2;
        user.GenerateIntegrityHash();
        database.Users.Update(user);

        using var removedKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var removedPublicKey = removedKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var authorizationId = Guid.NewGuid();
        var removalOperationId = Guid.NewGuid();
        var removalHash = Enumerable.Repeat((byte)0xA7, 32).ToArray();
        await database.UserMembershipAuthorizations.AddAsync(new UserMembershipAuthorization
        {
            AuthorizationId = authorizationId,
            UserId = user.UId,
            DeviceId = DeviceA,
            OriginInstanceId = InstanceA,
            SignPublicKey = removedPublicKey,
            SignPublicKeyHash = Hashing.SHA256Hash(removedPublicKey),
            AgreementPublicKeyHash = Enumerable.Repeat((byte)0xB8, 32).ToArray(),
            TlsCertFingerprint = new string('C', 64),
            DeviceType = DeviceType.WindowsPc,
            StartedMembershipEpoch = 1,
            EndedMembershipEpoch = 2,
            MinimumKeyEpoch = 1,
            MaximumKeyEpoch = 1,
            IsActive = false,
            RemovalOperationId = removalOperationId,
            RemovalOperationHash = removalHash,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            EndedAtUtc = DateTimeOffset.UtcNow
        });
        await database.UserOriginRemovalCutoffs.AddAsync(new UserOriginRemovalCutoff
        {
            UserId = user.UId,
            DeviceId = DeviceA,
            OriginInstanceId = InstanceA,
            UserKeyEpoch = 1,
            HighestAcceptedSnapshotRevision = 1,
            HighestAcceptedControlSequence = 0,
            ResultingMembershipEpoch = 2,
            AuthorizationId = authorizationId,
            RemovalOperationId = removalOperationId,
            RemovalOperationHash = removalHash,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await database.UnitOfWork.SaveChangesAsync();
        await service.RecalculateAsync(user.UId);

        var inbox = new UserSnapshotInboxService(
            database.Users,
            database.UserSyncSnapshots,
            database.UserRevisionKnowledge,
            identity,
            database.UnitOfWork,
            lifecycle,
            membership,
            database.DeletedUserBarriers,
            service);
        var preRemoval = CreateEnvelope(
            user,
            "PreRemoval",
            Stamp(2_000, 0, DeviceA, InstanceA),
            DeviceA,
            InstanceA,
            1,
            removedKey,
            membershipEpoch: 1);
        var accepted = await inbox.StoreAsync(preRemoval, Guid.NewGuid());
        MSTestAssert.AreEqual(UserSnapshotReceiptState.StoredPending, accepted.State);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.Matched,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("PreRemoval"))).State);

        var postCutoff = CreateEnvelope(
            user,
            "PostCutoff",
            Stamp(3_000, 0, DeviceA, InstanceA),
            DeviceA,
            InstanceA,
            2,
            removedKey,
            membershipEpoch: 1);
        var rejected = await inbox.StoreAsync(postCutoff, Guid.NewGuid());

        MSTestAssert.AreEqual(UserSnapshotReceiptState.Rejected, rejected.State);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.Matched,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("PreRemoval"))).State);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.NotFound,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("PostCutoff"))).State);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task OldKeyEpochSnapshot_CannotOverrideCurrentCanonicalIdentity()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var service = CreateService(database);
        var canonicalVersion = Stamp(3_000, 0, CanonicalDevice, CanonicalInstance);
        var user = await AddCanonicalAsync(database, service, "Current", canonicalVersion, keyEpoch: 2);
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var obsoleteVersion = Stamp(9_000, 0, DeviceA, InstanceA);
        await AddSnapshotAsync(database, CreateEnvelope(user, "Obsolete", obsoleteVersion, DeviceA, InstanceA, 1, key, keyEpoch: 1));

        await service.RecalculateAsync(user.UId);

        MSTestAssert.AreEqual(UserLoginIdentityMatchState.Matched,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("Current"))).State);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.NotFound,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("Obsolete"))).State);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task DeletedUserBarrier_RemovesProjectionAndBlocksLookup()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var service = CreateService(database);
        var user = await AddCanonicalAsync(
            database,
            service,
            "Deleted",
            Stamp(1_000, 0, CanonicalDevice, CanonicalInstance));
        using var pendingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var pending = CreateEnvelope(
            user,
            "PendingDeleted",
            Stamp(2_000, 0, DeviceA, InstanceA),
            DeviceA,
            InstanceA,
            1,
            pendingKey);
        await AddSnapshotAsync(database, pending);
        await service.RecalculateAsync(user.UId);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.Matched,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("PendingDeleted"))).State);

        await database.DeletedUserBarriers.AddAsync(new DeletedUserBarrier
        {
            UserId = user.UId,
            DeletionOperationId = Guid.NewGuid(),
            DeletionGeneration = Guid.NewGuid(),
            OriginDeviceId = CanonicalDevice,
            OriginInstanceId = CanonicalInstance,
            OriginSequence = 1,
            KeyEpoch = 1,
            MembershipEpoch = 1,
            DeletedAtUtc = DateTimeOffset.UtcNow,
            AppliedAtUtc = DateTimeOffset.UtcNow,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow,
            OperationHash = Enumerable.Repeat((byte)0x11, 32).ToArray(),
            OriginSignPublicKey = Enumerable.Repeat((byte)0x22, 32).ToArray(),
            OriginSignature = Enumerable.Repeat((byte)0x33, 64).ToArray()
        });
        await database.UnitOfWork.SaveChangesAsync();

        var projection = await service.RecalculateAsync(user.UId);

        MSTestAssert.IsNull(projection);
        MSTestAssert.IsNull(await database.Users.GetLoginIdentityStateAsync(user.UId));
        MSTestAssert.IsNull(await service.RecalculateAsync(user.UId));
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.NotFound,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("Deleted"))).State);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.NotFound,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("PendingDeleted"))).State);
    }

    private static async Task<string> ResolveConcurrentWinnerAsync(bool receiveBetaFirst)
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var service = CreateService(database);
        var canonicalVersion = Stamp(1_000, 0, CanonicalDevice, CanonicalInstance);
        var user = await AddCanonicalAsync(database, service, "Old", canonicalVersion);
        var alphaVersion = Stamp(2_000, 0, DeviceA, InstanceA);
        var betaVersion = Stamp(2_000, 0, DeviceB, InstanceB);
        using var keyA = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var keyB = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var alpha = CreateEnvelope(user, "Alpha", alphaVersion, DeviceA, InstanceA, 1, keyA);
        var beta = CreateEnvelope(user, "Beta", betaVersion, DeviceB, InstanceB, 1, keyB);

        foreach (var envelope in receiveBetaFirst ? new[] { beta, alpha } : new[] { alpha, beta })
        {
            await AddSnapshotAsync(database, envelope);
            await service.RecalculateAsync(user.UId);
        }

        MSTestAssert.AreEqual(UserLoginIdentityMatchState.NotFound,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("Old"))).State);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.NotFound,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("Alpha"))).State);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.Matched,
            (await service.FindByUsernameAsync(Encoding.UTF8.GetBytes("Beta"))).State);

        database.Db.ChangeTracker.Clear();
        var restarted = CreateService(database);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.Matched,
            (await restarted.FindByUsernameAsync(Encoding.UTF8.GetBytes("Beta"))).State);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.NotFound,
            (await restarted.FindByUsernameAsync(Encoding.UTF8.GetBytes("Old"))).State);
        var state = await database.Users.GetLoginIdentityStateAsync(user.UId)
            ?? throw new AssertFailedException("The durable login projection was not retained.");
        return Hashing.Verify(state.UsernameHash, beta.User.UsernameHash) &&
               Hashing.Verify(state.UsernameSalt, beta.User.UsernameSalt)
            ? "Beta"
            : "Unexpected";
    }

    private static UserLoginIdentityProjectionService CreateService(SqliteIntegrationTestDatabase database) =>
        new(
            database.Users,
            database.UserSyncSnapshots,
            new FakeUserMembershipAuthorizationService(),
            new UserLifecycleCoordinator(),
            database.UnitOfWork,
            database.DeletedUserBarriers);

    private static async Task<User> AddCanonicalAsync(
        SqliteIntegrationTestDatabase database,
        UserLoginIdentityProjectionService service,
        string username,
        SyncVersionStamp version,
        long keyEpoch = 1)
    {
        var salt = Hashing.GenerateSalt();
        var bytes = Encoding.UTF8.GetBytes(username);
        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            UId = Guid.NewGuid(),
            UsernameSalt = salt,
            UsernameHash = Hashing.SHA256Hash(bytes, salt),
            PasswordSalt = Enumerable.Repeat((byte)0x03, 32).ToArray(),
            EncryptedPayload = [0x10],
            EncryptedGeneralUserDataPayload = [0x20],
            EncryptedUserPasswordsDataPayload = [0x30],
            EncryptedUserDevicesDataPayload = [0x40],
            KeyEpoch = keyEpoch,
            MembershipEpoch = 1,
            LastModifiedAt = now,
            UserDataLastModifiedAt = now,
            GeneralUserDataLastModifiedAt = now,
            UserPasswordsDataLastModifiedAt = now,
            UserDevicesDataLastModifiedAt = now
        };
        user.SetGeneralUserDataVersion(version);
        user.GenerateIntegrityHash();
        await database.Users.AddAsync(user);
        await database.UnitOfWork.SaveChangesAsync();
        await service.SetCanonicalAsync(user, version);
        await database.UnitOfWork.SaveChangesAsync();
        CryptographicOperations.ZeroMemory(bytes);
        return user;
    }

    private static UserSnapshotEnvelope CreateEnvelope(
        User user,
        string username,
        SyncVersionStamp version,
        Guid originDeviceId,
        Guid originInstanceId,
        long revision,
        Key signingKey,
        long? keyEpoch = null,
        byte[]? usernameHash = null,
        byte[]? usernameSalt = null,
        long? membershipEpoch = null)
    {
        var usernameBytes = Encoding.UTF8.GetBytes(username);
        var effectiveUsernameSalt = usernameSalt?.ToArray() ?? Hashing.GenerateSalt();
        var effectiveUsernameHash = usernameHash?.ToArray() ?? Hashing.SHA256Hash(usernameBytes, effectiveUsernameSalt);
        var createdAt = DateTimeOffset.UtcNow.AddSeconds(revision);
        var payload = new UserSyncPayload
        {
            UId = user.UId,
            UsernameSalt = effectiveUsernameSalt,
            UsernameHash = effectiveUsernameHash,
            GeneralUserDataVersion = version,
            PasswordSalt = user.PasswordSalt.ToArray(),
            EncryptedPayload = [0x11],
            EncryptedGeneralUserDataPayload = [0x21],
            EncryptedUserPasswordsDataPayload = [0x31],
            EncryptedUserDevicesDataPayload = [0x41],
            UserDataLastModifiedAt = createdAt,
            GeneralUserDataLastModifiedAt = createdAt,
            UserPasswordsDataLastModifiedAt = createdAt,
            UserDevicesDataLastModifiedAt = createdAt,
            DeviceIds = [originDeviceId]
        };
        payload.IntegrityHash = SyncCryptoUtil.CalculateUserHash(payload, createdAt.ToUnixTimeMilliseconds());
        var envelope = new UserSnapshotEnvelope
        {
            UserId = user.UId,
            OriginDeviceId = originDeviceId,
            OriginInstanceId = originInstanceId,
            OriginRevision = revision,
            UserKeyEpoch = keyEpoch ?? user.KeyEpoch,
            MembershipEpoch = membershipEpoch ?? user.MembershipEpoch,
            CreatedAtUtc = createdAt,
            User = payload
        };
        UserSnapshotEnvelopeUtil.FillOriginAuthentication(envelope, new FakeDeviceIdentityService
        {
            LocalDeviceId = originDeviceId,
            OriginInstanceId = originInstanceId,
            SignPublicKey = signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            SignHandler = bytes => SignatureAlgorithm.Ed25519.Sign(signingKey, bytes)
        });
        CryptographicOperations.ZeroMemory(usernameBytes);
        return envelope;
    }

    private static async Task AddSnapshotAsync(
        SqliteIntegrationTestDatabase database,
        UserSnapshotEnvelope envelope,
        UserSyncSnapshotStatus status = UserSyncSnapshotStatus.Pending)
    {
        await database.UserSyncSnapshots.AddAsync(new UserSyncSnapshot
        {
            UserId = envelope.UserId,
            OriginDeviceId = envelope.OriginDeviceId,
            OriginInstanceId = envelope.OriginInstanceId,
            OriginRevision = envelope.OriginRevision,
            UserKeyEpoch = envelope.UserKeyEpoch,
            MembershipEpoch = envelope.MembershipEpoch,
            CreatedAtUtc = envelope.CreatedAtUtc,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            SnapshotHash = envelope.SnapshotHash.ToArray(),
            OriginSignPublicKey = envelope.OriginSignPublicKey.ToArray(),
            OriginSignature = envelope.OriginSignature.ToArray(),
            EnvelopePayload = JsonSerializer.SerializeToUtf8Bytes(
                envelope,
                BackendJsonSerializerContext.Default.UserSnapshotEnvelope),
            Status = status
        });
        await database.UnitOfWork.SaveChangesAsync();
    }

    private static SyncVersionStamp Stamp(long physical, long logical, Guid device, Guid instance) => new()
    {
        PhysicalTimeUnixMilliseconds = physical,
        LogicalCounter = logical,
        OriginDeviceId = device,
        OriginInstanceId = instance
    };
}
