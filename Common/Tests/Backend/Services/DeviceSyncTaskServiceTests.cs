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
    public async Task TryStart_WhenSendSucceeds_MarksQueueItemProcessedAndPersists()
    {
        using var setup = CreateSetup(sendResult: true);
        setup.Queue.Seed(CreateQueueItem(setup.Device.Id));

        MSTestAssert.IsTrue(setup.Service.TryStart(setup.Endpoint, setup.Device));
        await setup.Service.WaitForIdleAsync(setup.Device.Id).WaitAsync(TimeSpan.FromSeconds(10));

        MSTestAssert.AreEqual(1, setup.Transport.SendCalls);
        MSTestAssert.AreEqual(0, setup.Queue.Items.Count(item => item.ProcessedAt is null));
        MSTestAssert.AreEqual("192.168.1.25", setup.Transport.LastHost);
        MSTestAssert.AreEqual(26688, setup.Transport.LastPort);
        MSTestAssert.AreEqual("AABBCC", setup.Transport.LastFingerprint);
        MSTestAssert.AreEqual(1, setup.Transport.LastDeltas.Count);
        MSTestAssert.AreEqual(1, setup.UnitOfWork.SaveCalls);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task TryStart_WhenSendIsNotAcknowledged_KeepsQueueAndLearnedEndpointForRetry()
    {
        using var setup = CreateSetup(sendResult: false);
        setup.Queue.Seed(CreateQueueItem(setup.Device.Id));
        setup.EndpointRegistry.AddOrUpdate(setup.Endpoint);

        MSTestAssert.IsTrue(setup.Service.TryStart(setup.Endpoint, setup.Device));
        await setup.Service.WaitForIdleAsync(setup.Device.Id).WaitAsync(TimeSpan.FromSeconds(10));

        MSTestAssert.AreEqual(1, setup.Transport.SendCalls);
        MSTestAssert.IsTrue(
            setup.EndpointRegistry.TryGetByFingerprint(setup.Endpoint.TlsCertFingerprint, out var retryEndpoint));
        MSTestAssert.AreEqual(1, setup.Queue.Items.Count(item => item.ProcessedAt is null));
        MSTestAssert.AreEqual(0, setup.UnitOfWork.SaveCalls);

        MSTestAssert.IsNotNull(retryEndpoint);
        setup.Transport.SendResult = true;
        MSTestAssert.IsTrue(setup.Service.TryStart(retryEndpoint, setup.Device));
        await setup.Service.WaitForIdleAsync(setup.Device.Id).WaitAsync(TimeSpan.FromSeconds(10));
        MSTestAssert.AreEqual(2, setup.Transport.SendCalls);
        MSTestAssert.AreEqual(0, setup.Queue.Items.Count(item => item.ProcessedAt is null));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task TryStart_WhenOldSendFails_DoesNotEraseEndpointRefreshedDuringFlight()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var setup = CreateSetup(sendResult: false, sendGate: gate);
        setup.Queue.Seed(CreateQueueItem(setup.Device.Id));
        setup.EndpointRegistry.AddOrUpdate(setup.Endpoint);
        MSTestAssert.IsTrue(setup.Service.TryStart(setup.Endpoint, setup.Device));
        await WaitUntilAsync(() => setup.Transport.SendCalls == 1);

        var refreshed = new DiscoveredDeviceEndpoint
        {
            Host = "192.168.1.40", Port = 26689,
            TlsCertFingerprint = setup.Endpoint.TlsCertFingerprint,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2)
        };
        setup.EndpointRegistry.AddOrUpdate(refreshed);
        gate.SetResult(true);
        await setup.Service.WaitForIdleAsync(setup.Device.Id).WaitAsync(TimeSpan.FromSeconds(10));

        MSTestAssert.IsTrue(setup.EndpointRegistry.TryGetByFingerprint(refreshed.TlsCertFingerprint, out var retained));
        MSTestAssert.IsNotNull(retained);
        MSTestAssert.AreEqual(refreshed.Host, retained.Host);
        MSTestAssert.AreEqual(refreshed.Port, retained.Port);
        MSTestAssert.AreEqual(refreshed.ExpiresAtUtc, retained.ExpiresAtUtc,
            "A failed in-flight send must neither erase nor extend the newly learned route.");
        MSTestAssert.AreEqual(1, setup.Transport.SendCalls);
        MSTestAssert.AreEqual(1, setup.Queue.Items.Count(item => item.ProcessedAt is null));
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
        MSTestAssert.AreEqual(0, setup.Queue.Items.Count(item => item.ProcessedAt is null));
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
        MSTestAssert.AreEqual(0, setup.Queue.Items.Count(item => item.ProcessedAt is null));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task TryStart_WhileRunning_RerunsAntiEntropyWhenQueueBecomesEmpty()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var setup = CreateSetup(sendResult: true, sendGate: gate, enableAntiEntropy: true);
        setup.Queue.Seed(CreateQueueItem(setup.Device.Id));

        MSTestAssert.IsTrue(setup.Service.TryStart(setup.Endpoint, setup.Device));
        await WaitUntilAsync(() => setup.Transport.SendCalls == 1);

        // Endpoint-hint catch-up uses the same pending-start path but does not add a
        // durable queue item. The requested second session must still run anti-entropy.
        MSTestAssert.IsFalse(setup.Service.TryStart(setup.Endpoint, setup.Device));
        gate.SetResult(true);

        await setup.Service.WaitForIdleAsync(setup.Device.Id).WaitAsync(TimeSpan.FromSeconds(10));
        MSTestAssert.AreEqual(2, setup.Transport.InventoryCalls);
        MSTestAssert.AreEqual(0, setup.Queue.Items.Count(item => item.ProcessedAt is null));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task TryStart_WhenWorkIsRefreshedDuringSend_DoesNotAcknowledgeTheNewGeneration()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var setup = CreateSetup(sendResult: true, sendGate: gate);
        var queueItem = CreateQueueItem(setup.Device.Id);
        setup.Queue.Seed(queueItem);

        MSTestAssert.IsTrue(setup.Service.TryStart(setup.Endpoint, setup.Device));
        await WaitUntilAsync(() => setup.Transport.SendCalls == 1);

        await setup.Queue.EnqueueAsync(queueItem.SyncItemId, setup.Device.Id);
        gate.SetResult(true);

        await setup.Service.WaitForIdleAsync(setup.Device.Id).WaitAsync(TimeSpan.FromSeconds(10));
        MSTestAssert.AreEqual(2, setup.Transport.SendCalls,
            "The refreshed queue generation must be sent after the older in-flight payload completes.");
        MSTestAssert.AreEqual(0, setup.Queue.Items.Count(item => item.ProcessedAt is null));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task RepeatedEndpointHints_RefreshWithoutRestartingUnchangedPeers()
    {
        using var setup = CreateSetup(enableAntiEntropy: true);
        var userId = Guid.NewGuid();
        var thirdDevice = Clone(setup.Device, id: Guid.NewGuid());
        thirdDevice.TlsCertFingerprint = "CCDDEE";
        thirdDevice.GenerateIntegrityHash();
        ((FakeDeviceRepository)setup.Provider.GetRequiredService<IDeviceRepository>()).Seed(thirdDevice);
        await AddHintRouteAsync(setup, userId, thirdDevice, candidateEnabled: true);
        var hint = new PeerEndpointHint
        {
            DeviceId = thirdDevice.Id.ToString("N"), TlsCertFingerprint = thirdDevice.TlsCertFingerprint,
            Host = "192.168.1.30", Port = 26688
        };
        setup.Transport.InventoryReply.EndpointHints.Add(hint);
        var activations = (RecordingPendingSyncActivationService)setup.Provider.GetRequiredService<IPendingSyncActivationService>();

        MSTestAssert.IsTrue(setup.Service.TryStart(setup.Endpoint, setup.Device));
        await setup.Service.WaitForIdleAsync(setup.Device.Id).WaitAsync(TimeSpan.FromSeconds(10));
        MSTestAssert.AreEqual(1, activations.DeviceIds.Count);
        MSTestAssert.AreEqual(thirdDevice.Id, activations.DeviceIds.Single());
        MSTestAssert.IsTrue(setup.EndpointRegistry.TryGetByFingerprint(thirdDevice.TlsCertFingerprint, out var firstEndpoint));
        MSTestAssert.IsNotNull(firstEndpoint);
        MSTestAssert.IsNotNull(firstEndpoint.ExpiresAtUtc);

        MSTestAssert.IsTrue(setup.Service.TryStart(setup.Endpoint, setup.Device));
        await setup.Service.WaitForIdleAsync(setup.Device.Id).WaitAsync(TimeSpan.FromSeconds(10));
        MSTestAssert.AreEqual(2, setup.Transport.InventoryCalls);
        MSTestAssert.AreEqual(1, activations.DeviceIds.Count,
            "A repeated gossip hint must not activate an unchanged peer and perpetuate an idle sync loop.");

        hint.Port++;
        MSTestAssert.IsTrue(setup.Service.TryStart(setup.Endpoint, setup.Device));
        await setup.Service.WaitForIdleAsync(setup.Device.Id).WaitAsync(TimeSpan.FromSeconds(10));
        MSTestAssert.AreEqual(2, activations.DeviceIds.Count,
            "A changed authenticated endpoint must activate the peer again.");
        MSTestAssert.IsTrue(setup.EndpointRegistry.TryGetByFingerprint(thirdDevice.TlsCertFingerprint, out var changed));
        MSTestAssert.IsNotNull(changed);
        MSTestAssert.AreEqual(hint.Port, changed.Port);

        setup.Queue.Seed(CreateQueueItem(thirdDevice.Id));
        MSTestAssert.IsTrue(setup.Service.TryStart(setup.Endpoint, setup.Device));
        await setup.Service.WaitForIdleAsync(setup.Device.Id).WaitAsync(TimeSpan.FromSeconds(10));
        MSTestAssert.AreEqual(3, activations.DeviceIds.Count,
            "An unchanged endpoint must still retry pending delivery to that peer.");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task EndpointHint_BecomesEligibleDuringSnapshotApplication_IsRecheckedAgainstCommittedRoute(bool candidateEnabled)
    {
        var userId = Guid.NewGuid();
        var originId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var hash = new byte[32];
        var request = new UserSnapshotRequest
        {
            UserId = userId.ToString("N"), OriginDeviceId = originId.ToString("N"),
            OriginInstanceId = instanceId.ToString("N"), UserKeyEpoch = 1, MembershipEpoch = 1,
            OriginRevision = 1, ExpectedSnapshotHash = Google.Protobuf.ByteString.CopyFrom(hash)
        };
        DeviceSyncTaskSetup? activeSetup = null;
        Device? thirdDevice = null;
        var hintedBeforeApplication = true;
        var applyCalls = 0;
        var applier = new CallbackIncomingDeltaApplier(async (delta, ct) =>
        {
            applyCalls++;
            var currentSetup = activeSetup ?? throw new InvalidOperationException("The test setup is not initialized.");
            var currentDevice = thirdDevice ?? throw new InvalidOperationException("The test device is not initialized.");
            hintedBeforeApplication = currentSetup.EndpointRegistry.TryGetByFingerprint(currentDevice.TlsCertFingerprint, out _);
            ((FakeDeviceRepository)currentSetup.Provider.GetRequiredService<IDeviceRepository>()).Seed(currentDevice);
            await AddHintRouteAsync(currentSetup, userId, currentDevice, candidateEnabled);
            return new NetworkDeltaApplyResult(delta.Ts, new UserSnapshotReceiptResult(
                userId, originId, instanceId, 1, hash, UserSnapshotReceiptState.MergedImmediately));
        });
        using var setup = CreateSetup(enableAntiEntropy: true,
            snapshotAntiEntropy: new SingleRequestedSnapshotAntiEntropyService(request), incomingApplier: applier);
        activeSetup = setup;
        thirdDevice = Clone(setup.Device, id: Guid.NewGuid());
        thirdDevice.TlsCertFingerprint = "CCDDEE";
        thirdDevice.GenerateIntegrityHash();
        // Only the source route exists before applying the downloaded snapshot.
        await AddHintRouteAsync(setup, userId, candidate: null, candidateEnabled: false);
        setup.Transport.InventoryReply.EndpointHints.Add(new PeerEndpointHint
        {
            DeviceId = thirdDevice.Id.ToString("N"), TlsCertFingerprint = thirdDevice.TlsCertFingerprint,
            Host = "192.168.1.30", Port = 26688
        });
        setup.Transport.RequestedSnapshotDeltas = [new NetworkDelta { Ts = 1 }];

        MSTestAssert.IsTrue(setup.Service.TryStart(setup.Endpoint, setup.Device));
        await setup.Service.WaitForIdleAsync(setup.Device.Id).WaitAsync(TimeSpan.FromSeconds(10));
        MSTestAssert.AreEqual(1, applyCalls);
        MSTestAssert.AreEqual(1, setup.Transport.SnapshotRequestCalls);
        MSTestAssert.IsFalse(hintedBeforeApplication,
            "An unrecognized device must not be trusted merely because the source advertised its endpoint.");
        MSTestAssert.AreEqual(candidateEnabled,
            setup.EndpointRegistry.TryGetByFingerprint(thirdDevice.TlsCertFingerprint, out _));
        var activations = (RecordingPendingSyncActivationService)setup.Provider.GetRequiredService<IPendingSyncActivationService>();
        MSTestAssert.AreEqual(candidateEnabled ? 1 : 0, activations.DeviceIds.Count,
            "The second pass must still require an enabled, authenticated shared-user route.");
    }

    private static async Task AddHintRouteAsync(DeviceSyncTaskSetup setup, Guid userId, Device? candidate, bool candidateEnabled)
    {
        var local = new LocalUserDevice { UserId = userId, LocalDeviceIdentityId = setup.Identity.LocalDeviceId, IsSyncOn = true };
        local.GenerateIntegrityHash();
        await setup.Provider.GetRequiredService<ILocalUserDeviceRepository>().AddAsync(local);
        var sourceLink = new UserDevice { UserId = userId, DeviceId = setup.Device.Id, IsSyncOn = true };
        sourceLink.GenerateIntegrityHash();
        var links = setup.Provider.GetRequiredService<IUserDeviceRepository>();
        await links.AddAsync(sourceLink);
        if (candidate is null) return;
        var candidateLink = new UserDevice { UserId = userId, DeviceId = candidate.Id, IsSyncOn = candidateEnabled };
        candidateLink.GenerateIntegrityHash();
        await links.AddAsync(candidateLink);
    }

    private static DeviceSyncTaskSetup CreateSetup(
        bool sendResult = true,
        TaskCompletionSource<bool>? sendGate = null,
        bool enableAntiEntropy = false,
        IUserSnapshotAntiEntropyService? snapshotAntiEntropy = null,
        IIncomingDeltaApplierService? incomingApplier = null)
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
        if (enableAntiEntropy)
        {
            services.AddSingleton<ILocalUserDeviceRepository>(new FakeLocalUserDeviceRepository());
            services.AddSingleton<ISyncRouteRepository>(new AlwaysEligibleSyncRouteRepository());
            services.AddSingleton<IUserSnapshotAntiEntropyService>(snapshotAntiEntropy ?? new EmptyUserSnapshotAntiEntropyService());
            services.AddSingleton<IPendingSyncActivationService>(new RecordingPendingSyncActivationService());
            if (incomingApplier is not null)
                services.AddSingleton<IIncomingDeltaApplierService>(incomingApplier);
        }
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

    private sealed class AlwaysEligibleSyncRouteRepository : ISyncRouteRepository
    {
        public Task<bool> IsEligibleAsync(Guid userId, Guid remoteDeviceId, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> HasAnyEligibleAsync(
            IReadOnlyCollection<Guid> userIds,
            Guid remoteDeviceId,
            CancellationToken ct = default) => Task.FromResult(true);

        public Task<IReadOnlyList<Guid>> ListEligibleUserIdsAsync(
            IReadOnlyCollection<Guid> userIds,
            Guid remoteDeviceId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(userIds.ToArray());

        public Task<IReadOnlyList<Guid>> ListAllEligibleUserIdsAsync(
            Guid remoteDeviceId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

        public Task<bool> HasEligibleUserForDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class RecordingPendingSyncActivationService : IPendingSyncActivationService
    {
        public List<Guid> DeviceIds { get; } = [];
        public void ActivateDevices(IReadOnlyList<Device> devices) => DeviceIds.AddRange(devices.Select(device => device.Id));
        public Task ActivatePendingAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CallbackIncomingDeltaApplier(
        Func<NetworkDelta, CancellationToken, Task<NetworkDeltaApplyResult>> apply) : IIncomingDeltaApplierService
    {
        public Task<NetworkDeltaApplyResult> ApplyAsync(NetworkDelta delta, CancellationToken ct = default) => apply(delta, ct);
    }

    private sealed class SingleRequestedSnapshotAntiEntropyService(UserSnapshotRequest request) : IUserSnapshotAntiEntropyService
    {
        public Task<UserSnapshotInventoryExchangeRequest> BuildInventoryAsync(Guid peerDeviceId, CancellationToken ct = default) =>
            Task.FromResult(new UserSnapshotInventoryExchangeRequest());
        public Task<UserSnapshotInventoryExchangeReply> BuildInventoryReplyAsync(Guid peerDeviceId,
            UserSnapshotInventoryExchangeRequest remoteInventory, CancellationToken ct = default) =>
            Task.FromResult(new UserSnapshotInventoryExchangeReply());
        public IReadOnlyList<UserSnapshotRequest> FindMissingSnapshots(UserSnapshotInventoryExchangeRequest localInventory,
            IEnumerable<UserSnapshotUserInventory> remoteInventory) => [request];
        public Task<IReadOnlyList<NetworkDelta>> BuildRequestedSnapshotDeltasAsync(Guid peerDeviceId,
            IEnumerable<UserSnapshotRequest> requests, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NetworkDelta>>([]);
    }

    private sealed class EmptyUserSnapshotAntiEntropyService : IUserSnapshotAntiEntropyService
    {
        public Task<UserSnapshotInventoryExchangeRequest> BuildInventoryAsync(
            Guid peerDeviceId,
            CancellationToken ct = default) => Task.FromResult(new UserSnapshotInventoryExchangeRequest());

        public Task<UserSnapshotInventoryExchangeReply> BuildInventoryReplyAsync(
            Guid peerDeviceId,
            UserSnapshotInventoryExchangeRequest remoteInventory,
            CancellationToken ct = default) => Task.FromResult(new UserSnapshotInventoryExchangeReply());

        public IReadOnlyList<UserSnapshotRequest> FindMissingSnapshots(
            UserSnapshotInventoryExchangeRequest localInventory,
            IEnumerable<UserSnapshotUserInventory> remoteInventory) => Array.Empty<UserSnapshotRequest>();

        public Task<IReadOnlyList<NetworkDelta>> BuildRequestedSnapshotDeltasAsync(
            Guid peerDeviceId,
            IEnumerable<UserSnapshotRequest> requests,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NetworkDelta>>(Array.Empty<NetworkDelta>());
    }

}
