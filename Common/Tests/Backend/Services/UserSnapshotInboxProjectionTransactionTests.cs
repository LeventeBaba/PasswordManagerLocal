using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories.Sqlite;
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

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;
namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserSnapshotInboxProjectionTransactionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("Security")]
    public async Task ProjectionPersistenceFailure_RollsBackPendingSnapshotAndKeepsCanonicalIdentity(bool sqlite)
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateSharedAsync();
        using var session = new SqliteSession(new SqliteConnectionFactory(database.ConnectionString));
        var (users, snapshots, knowledge, barriers, writes) = SelectStorage(database, session, sqlite);
        var lifecycle = new UserLifecycleCoordinator();
        var membership = new FakeUserMembershipAuthorizationService();
        var canonicalProjection = new UserLoginIdentityProjectionService(
            users,
            snapshots,
            membership,
            lifecycle,
            writes,
            barriers);
        var canonicalVersion = Stamp(1_000, Guid.NewGuid(), Guid.NewGuid());
        var user = await AddCanonicalUserAsync(users, writes, canonicalProjection, "OldName", canonicalVersion);
        var throwingProjection = new ThrowingProjectionService(canonicalProjection);
        var clock = new RecordingVersionClock();
        var localIdentity = new FakeDeviceIdentityService
        {
            LocalDeviceId = Guid.NewGuid(),
            OriginInstanceId = Guid.NewGuid()
        };
        var inbox = new UserSnapshotInboxService(
            users,
            snapshots,
            knowledge,
            localIdentity,
            writes,
            lifecycle,
            membership,
            barriers,
            throwingProjection,
            clock);
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var originDevice = Guid.NewGuid();
        var originInstance = Guid.NewGuid();
        var envelope = CreateEnvelope(
            user,
            "NewName",
            Stamp(2_000, originDevice, originInstance),
            originDevice,
            originInstance,
            signingKey);

        await MSTestAssert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            inbox.StoreAsync(envelope, Guid.NewGuid()));

        database.Db.ChangeTracker.Clear();
        MSTestAssert.IsNull(await database.UserSyncSnapshots.GetAsync(
            user.UId,
            originDevice,
            originInstance,
            user.KeyEpoch));
        MSTestAssert.IsNull(await database.UserRevisionKnowledge.GetAsync(
            user.UId,
            originDevice,
            originInstance,
            user.KeyEpoch));
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.Matched,
            (await canonicalProjection.FindByUsernameAsync(Encoding.UTF8.GetBytes("OldName"))).State);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.NotFound,
            (await canonicalProjection.FindByUsernameAsync(Encoding.UTF8.GetBytes("NewName"))).State);
        MSTestAssert.AreEqual(0, clock.Observed.Count);
    }


    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task AcceptedPendingUsername_AdvancesDeterministicClockAfterAtomicCommit(bool sqlite)
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateSharedAsync();
        using var session = new SqliteSession(new SqliteConnectionFactory(database.ConnectionString));
        var (users, snapshots, knowledge, barriers, writes) = SelectStorage(database, session, sqlite);
        var lifecycle = new UserLifecycleCoordinator();
        var membership = new FakeUserMembershipAuthorizationService();
        var projection = new UserLoginIdentityProjectionService(
            users,
            snapshots,
            membership,
            lifecycle,
            writes,
            barriers);
        var canonicalVersion = Stamp(1_000, Guid.NewGuid(), Guid.NewGuid());
        var user = await AddCanonicalUserAsync(users, writes, projection, "OldName", canonicalVersion);
        var localIdentity = new FakeDeviceIdentityService
        {
            LocalDeviceId = Guid.NewGuid(),
            OriginInstanceId = Guid.NewGuid()
        };
        var clock = new RecordingVersionClock();
        var inbox = new UserSnapshotInboxService(
            users,
            snapshots,
            knowledge,
            localIdentity,
            writes,
            lifecycle,
            membership,
            barriers,
            projection,
            clock);
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var originDevice = Guid.NewGuid();
        var originInstance = Guid.NewGuid();
        var incomingVersion = Stamp(2_000, originDevice, originInstance);
        var envelope = CreateEnvelope(user, "NewName", incomingVersion, originDevice, originInstance, signingKey);

        var receipt = await inbox.StoreAsync(envelope, Guid.NewGuid());

        MSTestAssert.AreEqual(UserSnapshotReceiptState.StoredPending, receipt.State);
        MSTestAssert.AreEqual(1, clock.Observed.Count);
        MSTestAssert.AreEqual(incomingVersion, clock.Observed[0]);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.NotFound,
            (await projection.FindByUsernameAsync(Encoding.UTF8.GetBytes("OldName"))).State);
        MSTestAssert.AreEqual(UserLoginIdentityMatchState.Matched,
            (await projection.FindByUsernameAsync(Encoding.UTF8.GetBytes("NewName"))).State);
    }

    private static (IUserRepository Users, IUserSyncSnapshotRepository Snapshots, IUserRevisionKnowledgeRepository Knowledge,
        IDeletedUserBarrierRepository Barriers, IUnitOfWork Writes) SelectStorage(
        SqliteIntegrationTestDatabase database, SqliteSession session, bool sqlite) =>
        sqlite
            ? (new SqliteUserRepository(session), new SqliteUserSyncSnapshotRepository(session),
                new SqliteUserRevisionKnowledgeRepository(session), new FakeDeletedUserBarrierRepository(), session)
            : (database.Users, database.UserSyncSnapshots, database.UserRevisionKnowledge, database.DeletedUserBarriers, database.UnitOfWork);

    private static async Task<User> AddCanonicalUserAsync(
        IUserRepository users,
        IUnitOfWork writes,
        IUserLoginIdentityProjectionService projection,
        string username,
        SyncVersionStamp version)
    {
        var bytes = Encoding.UTF8.GetBytes(username);
        var salt = Hashing.GenerateSalt();
        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            UId = Guid.NewGuid(),
            UsernameHash = Hashing.SHA256Hash(bytes, salt),
            UsernameSalt = salt,
            PasswordSalt = Enumerable.Repeat((byte)0x03, 32).ToArray(),
            EncryptedPayload = [0x10],
            EncryptedGeneralUserDataPayload = [0x20],
            EncryptedUserPasswordsDataPayload = [0x30],
            EncryptedUserDevicesDataPayload = [0x40],
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
        await users.AddAsync(user);
        await writes.SaveChangesAsync();
        await projection.SetCanonicalAsync(user, version);
        await writes.SaveChangesAsync();
        CryptographicOperations.ZeroMemory(bytes);
        return user;
    }

    private static UserSnapshotEnvelope CreateEnvelope(
        User user,
        string username,
        SyncVersionStamp version,
        Guid originDevice,
        Guid originInstance,
        Key key)
    {
        var bytes = Encoding.UTF8.GetBytes(username);
        var salt = Hashing.GenerateSalt();
        var createdAt = DateTimeOffset.UtcNow;
        var payload = new UserSyncPayload
        {
            UId = user.UId,
            UsernameHash = Hashing.SHA256Hash(bytes, salt),
            UsernameSalt = salt,
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
            DeviceIds = [originDevice]
        };
        payload.IntegrityHash = SyncCryptoUtil.CalculateUserHash(payload, createdAt.ToUnixTimeMilliseconds());
        var envelope = new UserSnapshotEnvelope
        {
            UserId = user.UId,
            OriginDeviceId = originDevice,
            OriginInstanceId = originInstance,
            OriginRevision = 1,
            UserKeyEpoch = user.KeyEpoch,
            MembershipEpoch = user.MembershipEpoch,
            CreatedAtUtc = createdAt,
            User = payload
        };
        UserSnapshotEnvelopeUtil.FillOriginAuthentication(envelope, new FakeDeviceIdentityService
        {
            LocalDeviceId = originDevice,
            OriginInstanceId = originInstance,
            SignPublicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            SignHandler = data => SignatureAlgorithm.Ed25519.Sign(key, data)
        });
        CryptographicOperations.ZeroMemory(bytes);
        return envelope;
    }

    private static SyncVersionStamp Stamp(long physical, Guid device, Guid instance) => new()
    {
        PhysicalTimeUnixMilliseconds = physical,
        LogicalCounter = 0,
        OriginDeviceId = device,
        OriginInstanceId = instance
    };


}
