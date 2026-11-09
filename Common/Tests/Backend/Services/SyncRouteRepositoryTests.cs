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
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var localIdentity = CreateLocalIdentity();
        var targetDevice = CreateDevice("target");
        var unrelatedDevice = CreateDevice("unrelated");
        var eligibleUser = CreateUser();
        var localSyncOffUser = CreateUser();
        var remoteSyncOffUser = CreateUser();
        var deletedLinkUser = CreateUser();
        var unrelatedRouteUser = CreateUser();

        await database.DeviceIdentity.Create(localIdentity);
        await database.Devices.AddAsync(targetDevice);
        await database.Devices.AddAsync(unrelatedDevice);
        foreach (var user in new[] { eligibleUser, localSyncOffUser, remoteSyncOffUser, deletedLinkUser, unrelatedRouteUser })
            await database.Users.AddAsync(user);

        await database.LocalUserDevices.AddAsync(CreateLocalLink(eligibleUser.UId, localIdentity.Id, isSyncOn: true));
        await database.LocalUserDevices.AddAsync(CreateLocalLink(localSyncOffUser.UId, localIdentity.Id, isSyncOn: false));
        await database.LocalUserDevices.AddAsync(CreateLocalLink(remoteSyncOffUser.UId, localIdentity.Id, isSyncOn: true));
        await database.LocalUserDevices.AddAsync(CreateLocalLink(deletedLinkUser.UId, localIdentity.Id, isSyncOn: true));
        await database.LocalUserDevices.AddAsync(CreateLocalLink(unrelatedRouteUser.UId, localIdentity.Id, isSyncOn: true));

        await database.UserDevices.AddAsync(CreateRemoteLink(eligibleUser.UId, targetDevice.Id, isSyncOn: true, isDeleted: false));
        await database.UserDevices.AddAsync(CreateRemoteLink(localSyncOffUser.UId, targetDevice.Id, isSyncOn: true, isDeleted: false));
        await database.UserDevices.AddAsync(CreateRemoteLink(remoteSyncOffUser.UId, targetDevice.Id, isSyncOn: false, isDeleted: false));
        await database.UserDevices.AddAsync(CreateRemoteLink(deletedLinkUser.UId, targetDevice.Id, isSyncOn: true, isDeleted: true));
        await database.UserDevices.AddAsync(CreateRemoteLink(unrelatedRouteUser.UId, unrelatedDevice.Id, isSyncOn: true, isDeleted: false));
        await database.UnitOfWork.SaveChangesAsync();

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
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var user = CreateUser();
        user.EncryptedPayload = new byte[128 * 1024];
        user.EncryptedGeneralUserDataPayload = new byte[128 * 1024];
        user.EncryptedUserPasswordsDataPayload = new byte[128 * 1024];
        user.EncryptedUserDevicesDataPayload = new byte[128 * 1024];
        user.GenerateIntegrityHash();
        var device = CreateDevice("shape-test");
        var link = CreateRemoteLink(user.UId, device.Id, isSyncOn: true, isDeleted: false);

        await database.Users.AddAsync(user);
        await database.Devices.AddAsync(device);
        await database.UserDevices.AddAsync(link);
        await database.UnitOfWork.SaveChangesAsync();

        var relationshipOnly = await database.UserDevices.GetAsync(user.UId, device.Id);
        MSTestAssert.IsNotNull(relationshipOnly);
        MSTestAssert.IsNull(relationshipOnly.User);
        MSTestAssert.IsNull(relationshipOnly.Device);

        var withDevice = await database.UserDevices.GetWithDeviceAsync(user.UId, device.Id);
        MSTestAssert.IsNotNull(withDevice);
        MSTestAssert.IsNull(withDevice.User);
        MSTestAssert.IsNotNull(withDevice.Device);
        MSTestAssert.AreEqual(device.Id, withDevice.Device.Id);
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
