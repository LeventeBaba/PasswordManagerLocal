using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class SyncRouteRepositoryTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EligibleRouteQueries_FilterLocalAndRemoteSyncStateInDatabase()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var localIdentity = CreateLocalIdentity();
        var targetDevice = CreateDevice("target");
        var unrelatedDevice = CreateDevice("unrelated");
        var eligibleUser = CreateUser();
        var localSyncOffUser = CreateUser();
        var remoteSyncOffUser = CreateUser();
        var deletedLinkUser = CreateUser();
        var unrelatedRouteUser = CreateUser();

        database.Db.AddRange(
            localIdentity,
            targetDevice,
            unrelatedDevice,
            eligibleUser,
            localSyncOffUser,
            remoteSyncOffUser,
            deletedLinkUser,
            unrelatedRouteUser);

        database.Db.LocalUserDevices.AddRange(
            CreateLocalLink(eligibleUser.UId, localIdentity.Id, isSyncOn: true),
            CreateLocalLink(localSyncOffUser.UId, localIdentity.Id, isSyncOn: false),
            CreateLocalLink(remoteSyncOffUser.UId, localIdentity.Id, isSyncOn: true),
            CreateLocalLink(deletedLinkUser.UId, localIdentity.Id, isSyncOn: true),
            CreateLocalLink(unrelatedRouteUser.UId, localIdentity.Id, isSyncOn: true));

        database.Db.UserDevices.AddRange(
            CreateRemoteLink(eligibleUser.UId, targetDevice.Id, isSyncOn: true, isDeleted: false),
            CreateRemoteLink(localSyncOffUser.UId, targetDevice.Id, isSyncOn: true, isDeleted: false),
            CreateRemoteLink(remoteSyncOffUser.UId, targetDevice.Id, isSyncOn: false, isDeleted: false),
            CreateRemoteLink(deletedLinkUser.UId, targetDevice.Id, isSyncOn: true, isDeleted: true),
            CreateRemoteLink(unrelatedRouteUser.UId, unrelatedDevice.Id, isSyncOn: true, isDeleted: false));

        await database.Db.SaveChangesAsync();
        database.Db.ChangeTracker.Clear();

        var allUserIds = new[]
        {
            eligibleUser.UId,
            localSyncOffUser.UId,
            remoteSyncOffUser.UId,
            deletedLinkUser.UId,
            unrelatedRouteUser.UId
        };

        MSTestAssert.IsTrue(await database.SyncRoutes.IsEligibleAsync(eligibleUser.UId, targetDevice.Id));
        MSTestAssert.IsFalse(await database.SyncRoutes.IsEligibleAsync(localSyncOffUser.UId, targetDevice.Id));
        MSTestAssert.IsFalse(await database.SyncRoutes.IsEligibleAsync(remoteSyncOffUser.UId, targetDevice.Id));
        MSTestAssert.IsFalse(await database.SyncRoutes.IsEligibleAsync(deletedLinkUser.UId, targetDevice.Id));
        MSTestAssert.IsFalse(await database.SyncRoutes.IsEligibleAsync(unrelatedRouteUser.UId, targetDevice.Id));

        MSTestAssert.IsTrue(await database.SyncRoutes.HasAnyEligibleAsync(allUserIds, targetDevice.Id));
        CollectionAssert.AreEquivalent(
            new[] { eligibleUser.UId },
            (await database.SyncRoutes.ListEligibleUserIdsAsync(allUserIds, targetDevice.Id)).ToArray());
        MSTestAssert.IsTrue(await database.SyncRoutes.HasEligibleUserForDeviceAsync(targetDevice.Id));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task RelationshipQueries_LoadOnlyTheExplicitlyRequestedNavigationShape()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = CreateUser();
        user.EncryptedPayload = new byte[128 * 1024];
        user.EncryptedGeneralUserDataPayload = new byte[128 * 1024];
        user.EncryptedUserPasswordsDataPayload = new byte[128 * 1024];
        user.EncryptedUserDevicesDataPayload = new byte[128 * 1024];
        var device = CreateDevice("shape-test");
        var link = CreateRemoteLink(user.UId, device.Id, isSyncOn: true, isDeleted: false);

        database.Db.AddRange(user, device, link);
        await database.Db.SaveChangesAsync();
        database.Db.ChangeTracker.Clear();

        var relationshipOnly = await database.UserDevices.GetAsync(user.UId, device.Id);
        MSTestAssert.IsNotNull(relationshipOnly);
        MSTestAssert.IsNull(relationshipOnly.User);
        MSTestAssert.IsNull(relationshipOnly.Device);

        database.Db.ChangeTracker.Clear();
        var withDevice = await database.UserDevices.GetWithDeviceAsync(user.UId, device.Id);
        MSTestAssert.IsNotNull(withDevice);
        MSTestAssert.IsNull(withDevice.User);
        MSTestAssert.IsNotNull(withDevice.Device);
        MSTestAssert.AreEqual(device.Id, withDevice.Device.Id);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task GroupMembershipProjection_ReturnsIdsWithoutTrackingEncryptedUsers()
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateAsync();
        var user = CreateUser();
        user.EncryptedPayload = new byte[128 * 1024];
        user.EncryptedGeneralUserDataPayload = new byte[128 * 1024];
        user.EncryptedUserPasswordsDataPayload = new byte[128 * 1024];
        user.EncryptedUserDevicesDataPayload = new byte[128 * 1024];
        var group = new Group
        {
            EncryptedPayload = [9, 8, 7],
            Users = [user]
        };

        database.Db.Add(group);
        await database.Db.SaveChangesAsync();
        database.Db.ChangeTracker.Clear();

        var projected = await database.Groups.GetWithUserIdsAsNoTrackingAsync(group.Id);

        MSTestAssert.IsNotNull(projected);
        CollectionAssert.AreEqual(new[] { user.UId }, projected.UserIds);
        MSTestAssert.AreEqual(0, database.Db.ChangeTracker.Entries<User>().Count());
    }

    private static User CreateUser() => new()
    {
        UsernameHash = [1],
        UsernameSalt = [2],
        PasswordSalt = [3],
        EncryptedPayload = [4],
        EncryptedGeneralUserDataPayload = [5],
        EncryptedUserPasswordsDataPayload = [6],
        EncryptedUserDevicesDataPayload = [7]
    };

    private static Device CreateDevice(string fingerprintSuffix) => new()
    {
        PublicKey = [1],
        SignPublicKey = [2],
        TlsCertFingerprint = $"AA:{fingerprintSuffix}",
        DeviceType = DeviceType.WindowsPc,
        IsTrusted = true
    };

    private static LocalDeviceIdentity CreateLocalIdentity() => new()
    {
        AgreementPrivateKeyBlob = [1],
        SignPrivateKeyBlob = [2],
        PFXCertificate = [3],
        DeviceType = DeviceType.WindowsPc,
        IsSyncOn = true
    };

    private static LocalUserDevice CreateLocalLink(Guid userId, Guid localDeviceId, bool isSyncOn) => new()
    {
        UserId = userId,
        LocalDeviceIdentityId = localDeviceId,
        IsSyncOn = isSyncOn
    };

    private static UserDevice CreateRemoteLink(Guid userId, Guid deviceId, bool isSyncOn, bool isDeleted) => new()
    {
        UserId = userId,
        DeviceId = deviceId,
        IsSyncOn = isSyncOn,
        IsDeleted = isDeleted,
        DeletedAt = isDeleted ? DateTimeOffset.UtcNow : null
    };
}
