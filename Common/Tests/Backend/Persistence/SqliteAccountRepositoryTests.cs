using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
[TestCategory("Backend")]
[TestCategory("Integration")]
public sealed class SqliteAccountRepositoryTests
{
    [TestMethod]
    public async Task CanonicalLoginProjection_FollowsFlushCommitAndRollback()
    {
        await using var baseline = await SqliteRepositoryTestDatabase.CreateAsync();
        using var session = baseline.OpenSession();
        var users = new UserRepository(session);
        var projection = new UserLoginIdentityProjectionService(
            users, new FakeUserSyncSnapshotRepository(), new FakeUserMembershipAuthorizationService(),
            new UserLifecycleCoordinator(), session);
        var username = Encoding.UTF8.GetBytes("first-login");
        var user = CreateUser(username);

        await using (var transaction = await session.BeginTransactionAsync())
        {
            await users.AddAsync(user);
            await projection.SetCanonicalAsync(user, user.GetGeneralUserDataVersion());
            Assert.HasCount(0, await users.ListLoginLookupDataAsync());

            Assert.AreEqual(2, await session.SaveChangesAsync());
            var match = await projection.FindByUsernameAsync(username);
            Assert.AreEqual(UserLoginIdentityMatchState.Matched, match.State);
            Assert.AreEqual<Guid?>(user.UId, match.UserId);
            await transaction.CommitAsync();
        }

        var persisted = (await baseline.Users.ListLoginLookupDataAsync()).Single();
        Assert.AreEqual(user.UId, persisted.UId);
        CollectionAssert.AreEqual(user.UsernameHash, persisted.UsernameHash);

        await using (var transaction = await session.BeginTransactionAsync())
        {
            var renamed = (await users.GetByIdAsync(user.UId))!;
            var newUsername = Encoding.UTF8.GetBytes("second-login");
            renamed.UsernameHash = Hashing.SHA256Hash(newUsername, renamed.UsernameSalt);
            renamed.GeneralDataVersionLogicalCounter++;
            renamed.GenerateIntegrityHash();
            users.Update(renamed);
            await projection.SetCanonicalAsync(renamed, renamed.GetGeneralUserDataVersion());
            Assert.AreEqual(2, await session.SaveChangesAsync());
            Assert.AreEqual(UserLoginIdentityMatchState.Matched, (await projection.FindByUsernameAsync(newUsername)).State);
            await transaction.RollbackAsync();
            Assert.AreEqual(UserLoginIdentityMatchState.NotFound, (await projection.FindByUsernameAsync(newUsername)).State);
        }

        Assert.AreEqual(UserLoginIdentityMatchState.Matched, (await projection.FindByUsernameAsync(username)).State);
    }

    [TestMethod]
    public async Task UserUpdate_UsesOriginalBlobAfterBufferIsClearedAndRejectsStaleEpoch()
    {
        await using var baseline = await SqliteRepositoryTestDatabase.CreateAsync();
        var original = CreateUser([1]);
        await baseline.Users.AddAsync(original);
        await baseline.UnitOfWork.SaveChangesAsync();

        using var session = baseline.OpenSession();
        using var otherSession = baseline.OpenSession();
        var users = new UserRepository(session);
        var otherUsers = new UserRepository(otherSession);
        var first = (await users.GetByIdAsync(original.UId))!;
        CryptographicOperations.ZeroMemory(first.EncryptedUserPasswordsDataPayload);
        first.EncryptedUserPasswordsDataPayload = [0x77];
        first.GenerateIntegrityHash();
        users.Update(first);

        var writeTarget = (await users.GetByIdAsync(original.UId))!;
        Assert.AreSame(first, writeTarget);
        var persistedBeforeFlush = (await users.GetByIdAsNoTrackingAsync(original.UId))!;
        CollectionAssert.AreEqual(original.EncryptedUserPasswordsDataPayload, persistedBeforeFlush.EncryptedUserPasswordsDataPayload);

        writeTarget.LastModifiedAt = writeTarget.LastModifiedAt.AddSeconds(1);
        writeTarget.GenerateIntegrityHash();
        users.Update(writeTarget);
        Assert.AreEqual(1, await session.SaveChangesAsync());
        var afterFlush = (await baseline.Users.GetByIdAsNoTrackingAsync(original.UId))!;
        Assert.AreEqual(writeTarget.LastModifiedAt, afterFlush.LastModifiedAt);
        CollectionAssert.AreEqual(writeTarget.EncryptedUserPasswordsDataPayload, afterFlush.EncryptedUserPasswordsDataPayload);

        var latest = (await otherUsers.GetByIdAsync(original.UId))!;
        latest.MembershipEpoch++;
        latest.GenerateIntegrityHash();
        otherUsers.Update(latest);
        await otherSession.SaveChangesAsync();

        first.EncryptedUserDevicesDataPayload = [0x55];
        first.GenerateIntegrityHash();
        users.Update(first);
        var failure = await Assert.ThrowsExactlyAsync<StorageWriteException>(() => session.SaveChangesAsync());
        Assert.AreEqual(StorageWriteException.FailureKind.StaleRow, failure.Kind);
        Assert.AreEqual(typeof(User), failure.RowType);
        var stored = (await baseline.Users.GetByIdAsNoTrackingAsync(original.UId))!;
        Assert.AreEqual(latest.MembershipEpoch, stored.MembershipEpoch);
        CollectionAssert.AreEqual(new byte[] { 0x77 }, stored.EncryptedUserPasswordsDataPayload);
        CollectionAssert.AreEqual(original.EncryptedUserDevicesDataPayload, stored.EncryptedUserDevicesDataPayload);
        Assert.IsTrue(stored.IsIntegrityValid());
    }

    [TestMethod]
    public async Task SavedKeyWrite_IsImmediateAndSurvivesAnUnrelatedStagedUpdate()
    {
        await using var baseline = await SqliteRepositoryTestDatabase.CreateAsync();
        var user = CreateUser([2]);
        await baseline.Users.AddAsync(user);
        await baseline.UnitOfWork.SaveChangesAsync();
        using var session = baseline.OpenSession();
        var users = new UserRepository(session);

        var change = (await users.GetByIdAsync(user.UId))!;
        change.LastModifiedAt = change.LastModifiedAt.AddSeconds(1);
        change.GenerateIntegrityHash();
        users.Update(change);
        await users.UpdateSavedKeyAsync(user.UId, [0x91]);
        var beforeFlush = (await users.GetAllRememberMeEnabledUsersAsync()).Single();
        Assert.AreEqual(user.LastModifiedAt, beforeFlush.LastModifiedAt);
        CollectionAssert.AreEqual(new byte[] { 0x91 }, beforeFlush.SavedKey!);

        await session.SaveChangesAsync();
        var afterFlush = (await baseline.Users.GetAllRememberMeEnabledUsersAsync()).Single();
        Assert.AreEqual(change.LastModifiedAt, afterFlush.LastModifiedAt);
        CollectionAssert.AreEqual(new byte[] { 0x91 }, afterFlush.SavedKey!);
        Assert.IsTrue(afterFlush.IsIntegrityValid());

        await using (var transaction = await session.BeginTransactionAsync())
        {
            await users.UpdateSavedKeyAsync(user.UId, null);
            Assert.HasCount(0, await users.GetAllRememberMeEnabledUsersAsync());
            await transaction.RollbackAsync();
        }
        Assert.HasCount(1, await users.GetAllRememberMeEnabledUsersAsync());
    }

    [TestMethod]
    public async Task LoginProjectionUpdate_RejectsStaleVersionEvenWhenOnlyProjectionChanged()
    {
        await using var baseline = await SqliteRepositoryTestDatabase.CreateAsync();
        using var session = baseline.OpenSession();
        using var otherSession = baseline.OpenSession();
        var users = new UserRepository(session);
        var other = new UserRepository(otherSession);
        var user = CreateUser([3]);
        await users.AddAsync(user);
        await users.AddLoginIdentityStateAsync(new UserLoginIdentityState { UserId = user.UId, ConcurrencyVersion = 1 });
        await session.SaveChangesAsync();

        var stale = (await users.GetLoginIdentityStateAsync(user.UId))!;
        var next = (await other.GetLoginIdentityStateAsync(user.UId))!;
        next.Status = UserLoginIdentityStatus.InvalidSource;
        next.ConcurrencyVersion++;
        other.UpdateLoginIdentityState(next);
        await otherSession.SaveChangesAsync();

        users.DeleteLoginIdentityState(stale);
        var failure = await Assert.ThrowsExactlyAsync<StorageWriteException>(() => session.SaveChangesAsync());
        Assert.AreEqual(StorageWriteException.FailureKind.StaleRow, failure.Kind);
        Assert.AreEqual(typeof(UserLoginIdentityState), failure.RowType);
        Assert.AreEqual(UserLoginIdentityStatus.InvalidSource,
            (await baseline.Users.ListLoginIdentityStatesAsync()).Single().Status);
    }

    [TestMethod]
    public async Task GroupMembership_StagesExactPairsAndRetainsUnrelatedSharedMembership()
    {
        await using var baseline = await SqliteRepositoryTestDatabase.CreateAsync();
        using var session = baseline.OpenSession();
        var users = new UserRepository(session);
        var groups = new GroupRepository(session);
        var relationships = new SyncRelationshipReconciliationService(
            users, groups, new DeviceRepository(session), new UserDeviceRepository(session),
            new FakeDeviceIdentityService(), new FakeUserMembershipAuthorizationRepository());

        var first = CreateUser([4]);
        var second = CreateUser([5]);
        await users.AddAsync(first);
        await users.AddAsync(second);
        await session.SaveChangesAsync();
        var shared = new Group { EncryptedPayload = [0x10] };
        var unrelated = new Group { EncryptedPayload = [0x20] };
        shared.GenerateIntegrityHash();
        unrelated.GenerateIntegrityHash();

        await using (var transaction = await session.BeginTransactionAsync())
        {
            await groups.AddAsync(shared);
            await groups.AddAsync(unrelated);
            await relationships.SyncGroupUsersAsync(shared, [first.UId, second.UId, first.UId], default);
            groups.SetMember(unrelated, first, isMember: true);
            groups.SetMember(unrelated, second, isMember: true);
            groups.SetMember(unrelated, second, isMember: false);
            Assert.AreEqual(5, await session.SaveChangesAsync());
            await transaction.CommitAsync();
        }

        var loaded = (await users.GetByIdWithRelationsAsync(first.UId))!;
        await relationships.SyncUserGroupsAsync(loaded, [unrelated.Id], default);
        Assert.AreEqual(1, await session.SaveChangesAsync());
        Assert.AreEqual(second.UId, (await groups.ListUserIdsAsync(shared.Id)).Single());
        Assert.AreEqual(first.UId, (await groups.ListUserIdsAsync(unrelated.Id)).Single());
        Assert.AreEqual(shared.Id, (await groups.ListByUserWithUserIdsAsNoTrackingAsync(second.UId)).Single().Id);
        var persistedMembers = await baseline.UnitOfWork.ReadAsync("""
            SELECT "UsersUId" FROM "GroupMembers" WHERE "GroupsId" = $group;
            """, row => row.GetGuid(0), default, ("$group", shared.Id));
        CollectionAssert.AreEquivalent(new[] { second.UId }, persistedMembers);
        Assert.IsTrue((await groups.GetByIdWithUsersAsync(shared.Id))!.IsIntegrityValid());
    }

    [TestMethod]
    public async Task UserGraph_ReadsCommittedIdentityLinksAndRejectsTampering()
    {
        await using var baseline = await SqliteRepositoryTestDatabase.CreateAsync();
        using var session = baseline.OpenSession();
        var users = new UserRepository(session);
        var identities = new DeviceIdentityRepository(session);
        var devices = new DeviceRepository(session);
        var remoteLinks = new UserDeviceRepository(session);
        var localLinks = new LocalUserDeviceRepository(session);
        var routes = new SyncRouteRepository(session);

        var user = CreateUser([6]);
        var local = new LocalDeviceIdentity { DeviceType = DeviceType.WindowsPc, IsSyncOn = true };
        local.GenerateIntegrityHash();
        var remote = new Device
        {
            DeviceType = DeviceType.WindowsPc,
            TlsCertFingerprint = "A1B2",
            PublicKey = [1],
            SignPublicKey = [2]
        };
        await using (var transaction = await session.BeginTransactionAsync())
        {
            await users.AddAsync(user);
            await identities.Create(local);
            await devices.AddAsync(remote);
            await remoteLinks.AddAsync(new UserDevice { UserId = user.UId, DeviceId = remote.Id });
            await localLinks.AddAsync(new LocalUserDevice { UserId = user.UId, LocalDeviceIdentityId = local.Id });
            await session.SaveChangesAsync();
            Assert.IsTrue(await routes.IsEligibleAsync(user.UId, remote.Id));
            await transaction.CommitAsync();
        }

        var graph = (await users.GetByIdAsNoTrackingWithRelationsAsync(user.UId))!;
        Assert.AreEqual(remote.Id, graph.UserDevices.Single().DeviceId);
        Assert.AreEqual(local.Id, graph.LocalUserDevices.Single().LocalDeviceIdentityId);
        Assert.AreSame(graph, graph.UserDevices.Single().User);

        Assert.AreEqual(1, await baseline.UnitOfWork.ExecuteFixtureSqlAsync("""
            UPDATE "LocalUserDevices" SET "IsSyncOn" = 0 WHERE "UserId" = $user;
            """, ("$user", user.UId)));
        await Assert.ThrowsExactlyAsync<InvalidDataIntegrityException>(() => users.GetByIdWithRelationsAsync(user.UId));
    }

    private static User CreateUser(byte[] username)
    {
        var now = new DateTimeOffset(2026, 11, 7, 18, 0, 0, TimeSpan.Zero).AddTicks(23);
        var user = new User
        {
            UsernameSalt = Enumerable.Repeat((byte)0xA3, 32).ToArray(),
            PasswordSalt = Enumerable.Repeat((byte)0x33, 32).ToArray(),
            EncryptedPayload = [0x10],
            EncryptedGeneralUserDataPayload = [0x11],
            EncryptedUserPasswordsDataPayload = [0x12],
            EncryptedUserDevicesDataPayload = [0x13],
            KeyEpoch = 1,
            MembershipEpoch = 1,
            GeneralDataVersionPhysicalTimeUnixMilliseconds = 1_000,
            GeneralDataVersionOriginDeviceId = Guid.NewGuid(),
            GeneralDataVersionOriginInstanceId = Guid.NewGuid(),
            LastModifiedAt = now,
            UserDataLastModifiedAt = now,
            GeneralUserDataLastModifiedAt = now,
            UserPasswordsDataLastModifiedAt = now,
            UserDevicesDataLastModifiedAt = now
        };
        user.UsernameHash = Hashing.SHA256Hash(username, user.UsernameSalt);
        user.GenerateIntegrityHash();
        return user;
    }
}
