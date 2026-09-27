using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Security.Cryptography;
using System.Text;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class DeviceServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task SetLocalUserSyncOn_DisableThenEnable_PersistsAndQueuesRequiredCatchUp()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var users = host.Services.GetRequiredService<IUserService>();
        var service = host.Services.GetRequiredService<IDeviceService>();
        var userDevices = (FakeUserDeviceRepository)host.Services.GetRequiredService<IUserDeviceRepository>();
        var localUsers = (FakeLocalUserDeviceRepository)host.Services.GetRequiredService<ILocalUserDeviceRepository>();
        var queue = (FakeSyncQueueService)host.Services.GetRequiredService<ISyncQueueService>();
        var runtime = (FakeSyncRuntimeService)host.Services.GetRequiredService<ISyncRuntimeService>();
        var identity = (FakeDeviceIdentityService)host.Services.GetRequiredService<IDeviceIdentityService>();
        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("device_sync_toggle"));
        var userId = users.GetUidFromToken(token);
        var activeDevice = CreateRemoteDevice();
        var deletedDevice = CreateRemoteDevice();
        await userDevices.AddAsync(CreateLink(userId, activeDevice, true, false));
        await userDevices.AddAsync(CreateLink(userId, deletedDevice, false, true));
        var baselineRefreshCalls = runtime.RefreshSyncEnabledCalls;

        await service.SetLocalUserSyncOnAsync(token, false);

        MSTestAssert.IsFalse(await localUsers.IsSyncOnAsync(userId));
        MSTestAssert.AreEqual(baselineRefreshCalls + 1, runtime.RefreshSyncEnabledCalls);

        await service.SetLocalUserSyncOnAsync(token, true);

        MSTestAssert.IsTrue(await localUsers.IsSyncOnAsync(userId));
        MSTestAssert.AreEqual(baselineRefreshCalls + 2, runtime.RefreshSyncEnabledCalls);
        MSTestAssert.IsTrue(queue.UserCatchUpRequests.Contains((userId, activeDevice.Id)));
        MSTestAssert.IsTrue(queue.EnqueuedForDevices.Any(entry =>
            entry.TargetDeviceId == deletedDevice.Id &&
            entry.Item.ModelType == SyncModelType.UserDevice &&
            entry.Item.ChangeType == SyncChangeType.Deleted));
        MSTestAssert.AreNotEqual(identity.LocalDeviceId, activeDevice.Id);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task GetUserDevices_AddsMissingEncryptedMetadata_AndReturnsLocalAndRemoteDevices()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var users = host.Services.GetRequiredService<IUserService>();
        var service = host.Services.GetRequiredService<IDeviceService>();
        var userDevices = (FakeUserDeviceRepository)host.Services.GetRequiredService<IUserDeviceRepository>();
        var devices = (FakeDeviceRepository)host.Services.GetRequiredService<IDeviceRepository>();
        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("device_list"));
        var userId = users.GetUidFromToken(token);
        var remote = CreateRemoteDevice();
        remote.LastSeen = DateTime.UtcNow.AddMinutes(-2);
        devices.Seed(remote);
        await userDevices.AddAsync(CreateLink(userId, remote, true, false));

        var result = await service.GetUserDevicesAsync(token);

        MSTestAssert.HasCount(2, result);
        MSTestAssert.IsTrue(result[0].IsCurrentDevice);
        var remoteResponse = result.Single(item => item.DeviceId == remote.Id);
        MSTestAssert.IsFalse(remoteResponse.IsCurrentDevice);
        MSTestAssert.IsTrue(remoteResponse.IsSyncOn);
        MSTestAssert.IsFalse(remoteResponse.IsOnline);
        MSTestAssert.AreEqual(remote.TlsCertFingerprint, remoteResponse.TlsCertFingerprint);
        MSTestAssert.IsFalse(string.IsNullOrWhiteSpace(remoteResponse.Name));
        MSTestAssert.IsNotNull(remoteResponse.LastSync);
        MSTestAssert.IsNotNull(remoteResponse.LastSeen);
        MSTestAssert.IsNull(remoteResponse.LastLoginDate);
        MSTestAssert.IsNull(remoteResponse.PreviousLoginDate);

        var localResponse = result.Single(item => item.IsCurrentDevice);
        MSTestAssert.IsNull(localResponse.LastSync);
        MSTestAssert.IsNull(localResponse.LastSeen);
        MSTestAssert.IsNotNull(localResponse.LastLoginDate);
        MSTestAssert.IsNull(localResponse.PreviousLoginDate);
        MSTestAssert.IsTrue(localResponse.IsOnline);

        var bundle = await users.GetLoadAndVerifyUserDataBundleAsync(token);
        MSTestAssert.IsTrue(bundle.UserDevicesData.Devices.Any(device => device.Id == remote.Id));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task GetUserDevices_RequiresAuthenticatedProbeAndSyncEligibilityForRemoteOnline()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var users = host.Services.GetRequiredService<IUserService>();
        var service = host.Services.GetRequiredService<IDeviceService>();
        var userDevices = (FakeUserDeviceRepository)host.Services.GetRequiredService<IUserDeviceRepository>();
        var devices = (FakeDeviceRepository)host.Services.GetRequiredService<IDeviceRepository>();
        var endpoints = host.Services.GetRequiredService<IDiscoveredDeviceEndpointRegistry>();
        var transport = host.Services.GetRequiredService<FakeSyncTransportClientService>();
        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("device_online_status"));
        var userId = users.GetUidFromToken(token);
        var remote = CreateRemoteDevice();
        devices.Seed(remote);
        await userDevices.AddAsync(CreateLink(userId, remote, true, false));

        var beforeDiscovery = await service.GetUserDevicesAsync(token);
        MSTestAssert.IsFalse(beforeDiscovery.Single(item => item.DeviceId == remote.Id).IsOnline);

        endpoints.AddOrUpdate(new DiscoveredDeviceEndpoint
        {
            Host = "192.168.1.25",
            Port = 26688,
            TlsCertFingerprint = remote.TlsCertFingerprint
        });

        transport.ProbeResult = PasswordManagerLocal.Common.Backend.Sync.Presence.DevicePresenceProbeResult.Failed(
            PasswordManagerLocal.Common.Backend.Sync.Presence.DevicePresenceFailureKind.TlsOrFingerprintMismatch);
        var discoveryOnly = await service.GetUserDevicesAsync(token);
        MSTestAssert.IsFalse(discoveryOnly.Single(item => item.DeviceId == remote.Id).IsOnline);

        transport.ProbeResult = PasswordManagerLocal.Common.Backend.Sync.Presence.DevicePresenceProbeResult.Success;
        var authenticated = await service.GetUserDevicesAsync(token);
        MSTestAssert.IsTrue(authenticated.Single(item => item.DeviceId == remote.Id).IsOnline);

        await service.SetUserDeviceSyncOnAsync(token, remote.Id, false);

        var syncDisabled = await service.GetUserDevicesAsync(token);
        MSTestAssert.IsFalse(syncDisabled.Single(item => item.DeviceId == remote.Id).IsOnline);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task DeviceNames_AreTrimmedPersistedAndUniqueIgnoringCase()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var users = host.Services.GetRequiredService<IUserService>();
        var service = host.Services.GetRequiredService<IDeviceService>();
        var userDevices = (FakeUserDeviceRepository)host.Services.GetRequiredService<IUserDeviceRepository>();
        var devices = (FakeDeviceRepository)host.Services.GetRequiredService<IDeviceRepository>();
        var identity = (FakeDeviceIdentityService)host.Services.GetRequiredService<IDeviceIdentityService>();
        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("device_names"));
        var userId = users.GetUidFromToken(token);
        var remote = CreateRemoteDevice();
        devices.Seed(remote);
        await userDevices.AddAsync(CreateLink(userId, remote, true, false));
        await service.GetUserDevicesAsync(token);

        await service.SetLocalDeviceNameAsync(token, "  Main PC  ");
        await ExpectThrowsAsync<InvalidInputException>(() => service.SetUserDeviceNameAsync(token, remote.Id, "main pc"));
        await service.SetUserDeviceNameAsync(token, remote.Id, "Phone");

        var result = await service.GetUserDevicesAsync(token);
        MSTestAssert.AreEqual("Main PC", result.Single(item => item.DeviceId == identity.LocalDeviceId).Name);
        MSTestAssert.AreEqual("Phone", result.Single(item => item.DeviceId == remote.Id).Name);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task SetUserDeviceSyncOn_DisablingRemovesIdleCachedIdentity_EnablingQueuesCatchUp()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var users = host.Services.GetRequiredService<IUserService>();
        var service = host.Services.GetRequiredService<IDeviceService>();
        var userDevices = (FakeUserDeviceRepository)host.Services.GetRequiredService<IUserDeviceRepository>();
        var devices = (FakeDeviceRepository)host.Services.GetRequiredService<IDeviceRepository>();
        var queue = (FakeSyncQueueService)host.Services.GetRequiredService<ISyncQueueService>();
        var syncIdentities = (FakeSyncDeviceIdentityService)host.Services.GetRequiredService<ISyncDeviceIdentityService>();
        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("remote_sync_toggle"));
        var userId = users.GetUidFromToken(token);
        var remote = CreateRemoteDevice();
        devices.Seed(remote);
        await userDevices.AddAsync(CreateLink(userId, remote, true, false));
        syncIdentities.TryAdd(remote);

        await service.SetUserDeviceSyncOnAsync(token, remote.Id, false);

        var disabled = await userDevices.GetAsync(userId, remote.Id);
        MSTestAssert.IsNotNull(disabled);
        MSTestAssert.IsFalse(disabled.IsSyncOn);
        MSTestAssert.IsFalse(syncIdentities.ContainsId(remote.Id));
        MSTestAssert.IsTrue(queue.EnqueuedItems.Any(item => item.ModelType == SyncModelType.UserDevice));

        await service.SetUserDeviceSyncOnAsync(token, remote.Id, true);

        var enabled = await userDevices.GetAsync(userId, remote.Id);
        MSTestAssert.IsNotNull(enabled);
        MSTestAssert.IsTrue(enabled.IsSyncOn);
        MSTestAssert.IsTrue(queue.UserCatchUpRequests.Contains((userId, remote.Id)));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task UnblockUserDevice_ClearsSecurityStateAndQueuesUpdate()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var users = host.Services.GetRequiredService<IUserService>();
        var service = host.Services.GetRequiredService<IDeviceService>();
        var userDevices = (FakeUserDeviceRepository)host.Services.GetRequiredService<IUserDeviceRepository>();
        var devices = (FakeDeviceRepository)host.Services.GetRequiredService<IDeviceRepository>();
        var queue = (FakeSyncQueueService)host.Services.GetRequiredService<ISyncQueueService>();
        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("unblock_device"));
        var userId = users.GetUidFromToken(token);
        var remote = CreateRemoteDevice();
        remote.IsBlocked = true;
        remote.BlockedReason = "tampered payload";
        remote.BlockedAt = DateTimeOffset.UtcNow;
        remote.InvalidSyncAttemptCount = 5;
        remote.LastInvalidSyncAttemptAt = DateTimeOffset.UtcNow;
        remote.GenerateIntegrityHash();
        devices.Seed(remote);
        await userDevices.AddAsync(CreateLink(userId, remote, true, false));

        await service.UnblockUserDeviceAsync(token, remote.Id);

        MSTestAssert.IsFalse(remote.IsBlocked);
        MSTestAssert.IsNull(remote.BlockedReason);
        MSTestAssert.IsNull(remote.BlockedAt);
        MSTestAssert.AreEqual(0, remote.InvalidSyncAttemptCount);
        MSTestAssert.IsNull(remote.LastInvalidSyncAttemptAt);
        MSTestAssert.IsTrue(queue.EnqueuedItems.Any(item =>
            item.ModelId == remote.Id &&
            item.ModelType == SyncModelType.Device &&
            item.ChangeType == SyncChangeType.Updated));
        remote.VerifyIntegrity();
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task DisconnectUserDevice_WrongPasswordDoesNotModifyLink_CorrectPasswordDeletesIt()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var users = host.Services.GetRequiredService<IUserService>();
        var service = host.Services.GetRequiredService<IDeviceService>();
        var userDevices = (FakeUserDeviceRepository)host.Services.GetRequiredService<IUserDeviceRepository>();
        var devices = (FakeDeviceRepository)host.Services.GetRequiredService<IDeviceRepository>();
        var membership = host.Services.GetRequiredService<IUserMembershipAuthorizationService>();
        var membershipRows = host.Services.GetRequiredService<IUserMembershipAuthorizationRepository>();
        var cutoffs = host.Services.GetRequiredService<IUserOriginRemovalCutoffRepository>();
        var userRepository = host.Services.GetRequiredService<IUserRepository>();
        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("disconnect_device"));
        var userId = users.GetUidFromToken(token);
        var remote = CreateRemoteDevice();
        var remoteOrigin = Guid.NewGuid();
        devices.Seed(remote);
        await userDevices.AddAsync(CreateLink(userId, remote, true, false));
        var canonical = await userRepository.GetByIdAsync(userId) ?? throw new AssertFailedException("Registered user missing.");
        var addition = UserControlOperationEnvelopeUtil.CreateDeviceAdditionPayload(
            userId, canonical.KeyEpoch, canonical.MembershipEpoch, remote.Id, remoteOrigin,
            remote.SignPublicKey, remote.PublicKey, remote.TlsCertFingerprint, remote.DeviceType);
        await membership.AuthorizeAdditionAsync(addition, Guid.NewGuid(), RandomNumberGenerator.GetBytes(32));
        canonical.MembershipEpoch = addition.ResultingMembershipEpoch;
        canonical.GenerateIntegrityHash();
        userRepository.Update(canonical);
        await service.GetUserDevicesAsync(token);

        await ExpectThrowsAsync<InvalidInputException>(() =>
            service.DisconnectUserDeviceAsync(token, remote.Id, Encoding.UTF8.GetBytes("WrongPassword123!")));

        var unchanged = await userDevices.GetAsync(userId, remote.Id);
        MSTestAssert.IsNotNull(unchanged);
        MSTestAssert.IsFalse(unchanged.IsDeleted);

        var removal = await service.DisconnectUserDeviceAsync(token, remote.Id, Encoding.UTF8.GetBytes("P@ssw0rd12345678"));

        var deleted = await userDevices.GetAsync(userId, remote.Id);
        MSTestAssert.IsNotNull(deleted);
        MSTestAssert.IsTrue(deleted.IsDeleted);
        MSTestAssert.IsFalse(deleted.IsSyncOn);
        MSTestAssert.IsNotNull(deleted.DeletedAt);
        MSTestAssert.IsTrue(removal.Removed);
        MSTestAssert.IsTrue(removal.MayContainUnobservedChanges);
        MSTestAssert.AreNotEqual(Guid.Empty, removal.OperationId);
        MSTestAssert.AreEqual(3L, removal.ResultingMembershipEpoch);
        var endedAuthorization = (await membershipRows.ListForUserAsync(userId)).Single(row => row.DeviceId == remote.Id && row.OriginInstanceId == remoteOrigin);
        MSTestAssert.IsFalse(endedAuthorization.IsActive);
        MSTestAssert.IsNotNull(endedAuthorization.RemovalOperationId);
        MSTestAssert.IsNotEmpty(await cutoffs.ListForOriginAsync(userId, remote.Id, remoteOrigin));
        var bundle = await users.GetLoadAndVerifyUserDataBundleAsync(token);
        MSTestAssert.IsFalse(bundle.UserDevicesData.Devices.Any(device => device.Id == remote.Id));
        MSTestAssert.IsTrue(bundle.UserDevicesData.DeletedDevices.Any(device => device.Id == remote.Id));
        var remaining = await service.GetUserDevicesAsync(token);
        MSTestAssert.IsFalse(remaining.Any(item => item.DeviceId == remote.Id));
    }

    private static Device CreateRemoteDevice()
    {
        var device = new Device
        {
            Id = Guid.NewGuid(),
            PublicKey = Enumerable.Repeat((byte)1, 32).ToArray(),
            SignPublicKey = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray(),
            TlsCertFingerprint = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            DeviceType = DeviceType.AndroidMobile,
            IsTrusted = true,
            LastSeen = DateTime.UtcNow,
            LastSync = DateTime.UtcNow
        };
        device.GenerateIntegrityHash();
        return device;
    }

    private static UserDevice CreateLink(Guid userId, Device device, bool isSyncOn, bool isDeleted)
    {
        var link = new UserDevice
        {
            UserId = userId,
            DeviceId = device.Id,
            Device = device,
            IsSyncOn = isSyncOn,
            IsDeleted = isDeleted,
            DeletedAt = isDeleted ? DateTimeOffset.UtcNow : null,
            LastModifiedAt = DateTimeOffset.UtcNow
        };
        link.GenerateIntegrityHash();
        return link;
    }

    private static async Task ExpectThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
            MSTestAssert.Fail($"Expected exception: {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }
}
