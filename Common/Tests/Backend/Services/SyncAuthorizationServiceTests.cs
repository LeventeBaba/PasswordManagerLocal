using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;
namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class SyncAuthorizationServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task CanSendUser_RequiresLocalAndRemoteSyncRoute()
    {
        var setup = CreateSetup();
        var userId = Guid.NewGuid();
        var remoteId = Guid.NewGuid();
        await AddLocalLinkAsync(setup.LocalUsers, userId, true, setup.Identity.LocalDeviceId);
        await AddRemoteLinkAsync(setup.UserDevices, userId, remoteId, true, false);
        var item = new SyncItem
        {
            ModelId = userId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated
        };

        MSTestAssert.IsTrue(await setup.Service.CanSendAsync(item, remoteId));

        await AddLocalLinkAsync(setup.LocalUsers, userId, false, setup.Identity.LocalDeviceId);
        MSTestAssert.IsFalse(await setup.Service.CanSendAsync(item, remoteId));

        await AddLocalLinkAsync(setup.LocalUsers, userId, true, setup.Identity.LocalDeviceId);
        await AddRemoteLinkAsync(setup.UserDevices, userId, remoteId, false, false);
        MSTestAssert.IsFalse(await setup.Service.CanSendAsync(item, remoteId));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task CanSendGenericDeletedUser_IsRejectedEvenIfRouteWasRemoved()
    {
        var setup = CreateSetup();
        var item = new SyncItem
        {
            ModelId = Guid.NewGuid(),
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Deleted
        };

        MSTestAssert.IsFalse(await setup.Service.CanSendAsync(item, Guid.NewGuid()));
        MSTestAssert.IsFalse(await setup.Service.CanSendAsync(item, setup.Identity.LocalDeviceId));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task CanReceiveUserDeviceForLocalDevice_AllowsOnlyDeletion()
    {
        var setup = CreateSetup();
        var userId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        await AddLocalLinkAsync(setup.LocalUsers, userId, true, setup.Identity.LocalDeviceId);
        var payload = new SyncDeltaPayload
        {
            ModelId = Guid.NewGuid(),
            ModelType = SyncModelType.UserDevice,
            ChangeType = SyncChangeType.Updated,
            UserDevice = new UserDeviceSyncPayload
            {
                UserId = userId,
                DeviceId = setup.Identity.LocalDeviceId,
                IsSyncOn = true,
                IsDeleted = false
            }
        };

        MSTestAssert.IsFalse(await setup.Service.CanReceiveAsync(payload, sourceId));

        payload.ChangeType = SyncChangeType.Deleted;
        payload.UserDevice.IsDeleted = true;
        MSTestAssert.IsTrue(await setup.Service.CanReceiveAsync(payload, sourceId));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task CanReceiveGroup_AllowsWhenAnyPayloadUserHasEnabledRoute()
    {
        var setup = CreateSetup();
        var disabledUser = Guid.NewGuid();
        var enabledUser = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        await AddLocalLinkAsync(setup.LocalUsers, disabledUser, false, setup.Identity.LocalDeviceId);
        await AddLocalLinkAsync(setup.LocalUsers, enabledUser, true, setup.Identity.LocalDeviceId);
        await AddRemoteLinkAsync(setup.UserDevices, enabledUser, sourceId, true, false);
        var payload = new SyncDeltaPayload
        {
            ModelId = Guid.NewGuid(),
            ModelType = SyncModelType.Group,
            ChangeType = SyncChangeType.Updated,
            Group = new GroupSyncPayload
            {
                Id = Guid.NewGuid(),
                UserIds = [disabledUser, enabledUser]
            }
        };

        MSTestAssert.IsTrue(await setup.Service.CanReceiveAsync(payload, sourceId));

        await AddRemoteLinkAsync(setup.UserDevices, enabledUser, sourceId, false, false);
        MSTestAssert.IsFalse(await setup.Service.CanReceiveAsync(payload, sourceId));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task CanReceiveGroupWithoutPayloadUsers_FallsBackToPersistedMembership()
    {
        var setup = CreateSetup();
        var userId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var group = new Group { Id = Guid.NewGuid() };
        group.Users.Add(new User { UId = userId });
        setup.Groups.Seed(group);
        await AddLocalLinkAsync(setup.LocalUsers, userId, true, setup.Identity.LocalDeviceId);
        await AddRemoteLinkAsync(setup.UserDevices, userId, sourceId, true, false);
        var payload = new SyncDeltaPayload
        {
            ModelId = group.Id,
            ModelType = SyncModelType.Group,
            ChangeType = SyncChangeType.Updated,
            Group = new GroupSyncPayload { Id = group.Id }
        };

        MSTestAssert.IsTrue(await setup.Service.CanReceiveAsync(payload, sourceId));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task HasEligibleUserForDevice_IgnoresDisabledAndDeletedLinks()
    {
        var setup = CreateSetup();
        var remoteId = Guid.NewGuid();
        var disabledUser = Guid.NewGuid();
        var enabledUser = Guid.NewGuid();
        await AddLocalLinkAsync(setup.LocalUsers, disabledUser, true, setup.Identity.LocalDeviceId);
        await AddLocalLinkAsync(setup.LocalUsers, enabledUser, true, setup.Identity.LocalDeviceId);
        await AddRemoteLinkAsync(setup.UserDevices, disabledUser, remoteId, false, false);
        await AddRemoteLinkAsync(setup.UserDevices, enabledUser, remoteId, true, true);

        MSTestAssert.IsFalse(await setup.Service.HasEligibleUserForDeviceAsync(remoteId));

        await AddRemoteLinkAsync(setup.UserDevices, enabledUser, remoteId, true, false);
        MSTestAssert.IsTrue(await setup.Service.HasEligibleUserForDeviceAsync(remoteId));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task AuthorizationIsRejectedWhenLocalSynchronizationIsDisabled()
    {
        var setup = CreateSetup();
        await setup.Identity.SetSyncOnAsync(false);
        var userId = Guid.NewGuid();
        var remoteId = Guid.NewGuid();
        await AddLocalLinkAsync(setup.LocalUsers, userId, true, setup.Identity.LocalDeviceId);
        await AddRemoteLinkAsync(setup.UserDevices, userId, remoteId, true, false);

        MSTestAssert.IsFalse(await setup.Service.CanSendAsync(new SyncItem
        {
            ModelId = userId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated
        }, remoteId));

        MSTestAssert.IsFalse(await setup.Service.CanReceiveAsync(new SyncDeltaPayload
        {
            ModelId = userId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated
        }, remoteId));
    }

    private static SyncAuthorizationSetup CreateSetup()
    {
        var groups = new FakeGroupRepository();
        var devices = new FakeDeviceRepository();
        var userDevices = new FakeUserDeviceRepository();
        var localUsers = new FakeLocalUserDeviceRepository();
        var syncRoutes = new FakeSyncRouteRepository(userDevices, localUsers);
        var identity = new FakeDeviceIdentityService { LocalDeviceId = Guid.NewGuid() };
        var service = new SyncAuthorizationService(groups, devices, userDevices, localUsers, syncRoutes, identity);
        return new SyncAuthorizationSetup(service, groups, userDevices, localUsers, identity);
    }

    private static Task AddLocalLinkAsync(FakeLocalUserDeviceRepository repository, Guid userId, bool isSyncOn, Guid localDeviceId) =>
        repository.AddAsync(new LocalUserDevice
        {
            UserId = userId,
            LocalDeviceIdentityId = localDeviceId,
            IsSyncOn = isSyncOn
        });

    private static Task AddRemoteLinkAsync(FakeUserDeviceRepository repository, Guid userId, Guid deviceId, bool isSyncOn, bool isDeleted) =>
        repository.AddAsync(new UserDevice
        {
            UserId = userId,
            DeviceId = deviceId,
            Device = new Device { Id = deviceId },
            IsSyncOn = isSyncOn,
            IsDeleted = isDeleted
        });

}
