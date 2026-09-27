using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;
namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class DeviceSyncTaskServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void TryStart_RejectsInvalidOrUnsafeTargets()
    {
        using var setup = CreateSetup();
        var validEndpoint = setup.Endpoint;
        var validDevice = setup.Device;

        setup.Identity.IsSyncOn = false;
        MSTestAssert.IsFalse(setup.Service.TryStart(validEndpoint, validDevice));
        setup.Identity.IsSyncOn = true;

        MSTestAssert.IsFalse(setup.Service.TryStart(validEndpoint, Clone(validDevice, id: Guid.Empty)));
        MSTestAssert.IsFalse(setup.Service.TryStart(validEndpoint, Clone(validDevice, isTrusted: false)));
        MSTestAssert.IsFalse(setup.Service.TryStart(validEndpoint, Clone(validDevice, isBlocked: true)));
        MSTestAssert.IsFalse(setup.Service.TryStart(validEndpoint, Clone(validDevice, id: setup.Identity.LocalDeviceId)));
        MSTestAssert.IsFalse(setup.Service.TryStart(new DiscoveredDeviceEndpoint
        {
            Host = string.Empty,
            Port = validEndpoint.Port,
            TlsCertFingerprint = validEndpoint.TlsCertFingerprint
        }, validDevice));
        MSTestAssert.IsFalse(setup.Service.TryStart(new DiscoveredDeviceEndpoint
        {
            Host = validEndpoint.Host,
            Port = 0,
            TlsCertFingerprint = validEndpoint.TlsCertFingerprint
        }, validDevice));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task TryStart_WhenSendSucceeds_RemovesQueueItemAndPersists()
    {
        using var setup = CreateSetup(sendResult: true);
        setup.Queue.Seed(CreateQueueItem(setup.Device.Id));

        MSTestAssert.IsTrue(setup.Service.TryStart(setup.Endpoint, setup.Device));
        await setup.Service.WaitForIdleAsync(setup.Device.Id).WaitAsync(TimeSpan.FromSeconds(10));

        MSTestAssert.AreEqual(1, setup.Transport.SendCalls);
        MSTestAssert.AreEqual(0, setup.Queue.Items.Count);
        MSTestAssert.AreEqual("192.168.1.25", setup.Transport.LastHost);
        MSTestAssert.AreEqual(26688, setup.Transport.LastPort);
        MSTestAssert.AreEqual("AABBCC", setup.Transport.LastFingerprint);
        MSTestAssert.AreEqual(1, setup.Transport.LastDeltas.Count);
        MSTestAssert.AreEqual(1, setup.UnitOfWork.SaveCalls);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task TryStart_WhenTransportFails_KeepsQueueItemAndEvictsEndpoint()
    {
        using var setup = CreateSetup(sendResult: false);
        setup.Queue.Seed(CreateQueueItem(setup.Device.Id));
        setup.EndpointRegistry.AddOrUpdate(setup.Endpoint);

        MSTestAssert.IsTrue(setup.Service.TryStart(setup.Endpoint, setup.Device));
        await setup.Service.WaitForIdleAsync(setup.Device.Id).WaitAsync(TimeSpan.FromSeconds(10));

        MSTestAssert.AreEqual(1, setup.Transport.SendCalls);
        MSTestAssert.IsFalse(
            setup.EndpointRegistry.TryGetByFingerprint(setup.Endpoint.TlsCertFingerprint, out _));
        MSTestAssert.AreEqual(1, setup.Queue.Items.Count);
        MSTestAssert.AreEqual(0, setup.UnitOfWork.SaveCalls);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task TryStart_WhileDeviceTaskIsRunning_RejectsDuplicateStart()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var setup = CreateSetup(sendResult: true, sendGate: gate);
        setup.Queue.Seed(CreateQueueItem(setup.Device.Id));

        MSTestAssert.IsTrue(setup.Service.TryStart(setup.Endpoint, setup.Device));
        await WaitUntilAsync(() => setup.Transport.SendCalls == 1);
        MSTestAssert.IsFalse(setup.Service.TryStart(setup.Endpoint, setup.Device));

        gate.SetResult(true);
        await setup.Service.WaitForIdleAsync(setup.Device.Id).WaitAsync(TimeSpan.FromSeconds(10));
        MSTestAssert.AreEqual(0, setup.Queue.Items.Count);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task TryStart_WhileRunning_RemembersPendingKickAndRestartsAfterFirstAttemptStops()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var setup = CreateSetup(sendResult: true, sendGate: gate);
        setup.Transport.SendResults.Enqueue(false);
        setup.Transport.SendResults.Enqueue(true);
        setup.Queue.Seed(CreateQueueItem(setup.Device.Id));

        MSTestAssert.IsTrue(setup.Service.TryStart(setup.Endpoint, setup.Device));
        await WaitUntilAsync(() => setup.Transport.SendCalls == 1);
        MSTestAssert.IsFalse(setup.Service.TryStart(setup.Endpoint, setup.Device));

        gate.SetResult(true);

        await setup.Service.WaitForIdleAsync(setup.Device.Id).WaitAsync(TimeSpan.FromSeconds(10));
        MSTestAssert.AreEqual(2, setup.Transport.SendCalls);
        MSTestAssert.AreEqual(0, setup.Queue.Items.Count);
    }

    private static DeviceSyncTaskSetup CreateSetup(bool sendResult = true, TaskCompletionSource<bool>? sendGate = null)
    {
        var device = new Device
        {
            Id = Guid.NewGuid(),
            PublicKey = [1],
            SignPublicKey = [2],
            TlsCertFingerprint = "AABBCC",
            DeviceType = DeviceType.WindowsPc,
            IsTrusted = true,
            IsBlocked = false
        };
        device.GenerateIntegrityHash();
        var endpoint = new DiscoveredDeviceEndpoint
        {
            Host = "192.168.1.25",
            Port = 26688,
            TlsCertFingerprint = "AABBCC"
        };
        var identity = new FakeDeviceIdentityService
        {
            IsInitialized = true,
            IsSyncOn = true,
            LocalDeviceId = Guid.NewGuid(),
            SignPublicKey = [9],
            FingerprintHex = "LOCAL"
        };
        var queue = new FakeSyncQueueRepository();
        var devices = new FakeDeviceRepository();
        devices.Seed(device);
        var unitOfWork = new FakeUnitOfWork();
        var builder = new FakeOutgoingDeltaBuilderService
        {
            Result = new NetworkDelta { Entity = "test" }
        };
        var transport = new FakeSyncTransportClientService
        {
            SendResult = sendResult,
            SendGate = sendGate
        };
        var endpointRegistry = new DiscoveredDeviceEndpointRegistry();
        var syncIdentities = new FakeSyncDeviceIdentityService();
        syncIdentities.TryAdd(device);
        var services = new ServiceCollection();
        services.AddSingleton<ISyncQueueRepository>(queue);
        services.AddSingleton<IOutgoingDeltaBuilderService>(builder);
        services.AddSingleton<ISyncAuthorizationService>(new FakeSyncAuthorizationService());
        services.AddSingleton<IUnitOfWork>(unitOfWork);
        services.AddSingleton<IDeviceRepository>(devices);
        services.AddSingleton<IUserDeviceRepository>(new FakeUserDeviceRepository());
        services.AddSingleton<ISyncTombstoneRepository>(new FakeSyncTombstoneRepository());
        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        var service = new DeviceSyncTaskService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            transport,
            syncIdentities,
            endpointRegistry,
            identity);

        return new DeviceSyncTaskSetup(
            service,
            provider,
            queue,
            unitOfWork,
            transport,
            endpointRegistry,
            identity,
            device,
            endpoint);
    }

    private static SyncQueueItem CreateQueueItem(Guid deviceId)
    {
        var syncItem = new SyncItem
        {
            ModelId = Guid.NewGuid(),
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated
        };
        return new SyncQueueItem
        {
            DeviceId = deviceId,
            SyncItemId = syncItem.Id,
            SyncItem = syncItem
        };
    }

    private static Device Clone(Device source, Guid? id = null, bool? isTrusted = null, bool? isBlocked = null) =>
        new()
        {
            Id = id ?? source.Id,
            PublicKey = source.PublicKey.ToArray(),
            SignPublicKey = source.SignPublicKey.ToArray(),
            TlsCertFingerprint = source.TlsCertFingerprint,
            DeviceType = source.DeviceType,
            IsTrusted = isTrusted ?? source.IsTrusted,
            IsBlocked = isBlocked ?? source.IsBlocked
        };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeoutAt)
                MSTestAssert.Fail("Timed out waiting for the background sync task.");

            await Task.Delay(10);
        }
    }

}
