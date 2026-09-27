using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;
namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class NetworkDeltaServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task Apply_WhenLocalSyncIsDisabled_RejectsBeforeProcessingEnvelope()
    {
        var identity = new FakeDeviceIdentityService { IsSyncOn = false };
        var service = CreateService(identity, new FakeDeviceRepository());

        await ExpectThrowsAsync<SyncRouteDisabledException>(() => service.ApplyAsync(new NetworkDelta()));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task Apply_WhenDeltaTargetsAnotherDevice_RejectsIt()
    {
        using var setup = await CreateValidDeltaAsync();
        setup.Delta.RecipientDeviceId = Guid.NewGuid().ToString("N");
        var service = CreateService(setup.Recipient, new FakeDeviceRepository());

        await ExpectThrowsAsync<UnauthorizedAccessException>(() => service.ApplyAsync(setup.Delta));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task Apply_WhenSignatureIsTampered_RejectsBeforeDatabaseLookup()
    {
        using var setup = await CreateValidDeltaAsync();
        setup.Delta.Sig[0] ^= 0xFF;
        var service = CreateService(setup.Recipient, new FakeDeviceRepository());

        await ExpectThrowsAsync<InvalidDataException>(() => service.ApplyAsync(setup.Delta));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task Apply_WhenSignedSourceDeviceIsUnknown_RejectsIt()
    {
        using var setup = await CreateValidDeltaAsync();
        var service = CreateService(setup.Recipient, new FakeDeviceRepository());

        await ExpectThrowsAsync<UnauthorizedAccessException>(() => service.ApplyAsync(setup.Delta));
    }

    private static NetworkDeltaService CreateService(IDeviceIdentityService identity, FakeDeviceRepository devices)
    {
        var users = new InMemoryUserRepository();
        var groups = new FakeGroupRepository();
        var userDevices = new FakeUserDeviceRepository();
        var localUsers = new FakeLocalUserDeviceRepository();
        var syncRoutes = new FakeSyncRouteRepository(userDevices, localUsers);
        var tombstones = new FakeSyncTombstoneRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var syncQueueService = new FakeSyncQueueService();
        var syncDeviceIdentities = new FakeSyncDeviceIdentityService();
        var authorization = new FakeSyncAuthorizationService();
        var runtime = new FakeSyncRuntimeService();
        var auth = new FakeAuthService();
        var interactiveSessions = new FakeInteractiveSessionStateService(auth);
        var unitOfWork = new FakeUnitOfWork();

        var membership = new FakeUserMembershipAuthorizationService();
        var membershipRepository = new FakeUserMembershipAuthorizationRepository();
        var relationships = new SyncRelationshipReconciliationService(users, groups, devices, userDevices, identity, membershipRepository);
        var userDeltaApplier = new UserDeltaApplierService(users, tombstones, syncQueueService);
        var protocol = new NetworkDeltaProtocolService(devices, groups, userDevices, authorization, identity, membership);
        var replay = new NetworkDeltaReplayService(groups, devices, userDevices, tombstones);
        var payloadApplier = new NetworkDeltaPayloadApplierService(
            userDeltaApplier,
            users,
            groups,
            devices,
            userDevices,
            syncRoutes,
            tombstones,
            syncQueue,
            syncDeviceIdentities,
            identity,
            authorization,
            interactiveSessions,
            relationships,
            membershipRepository);
        var lifecycle = new NetworkDeltaLifecycleService(
            users,
            devices,
            userDevices,
            tombstones,
            syncQueue,
            syncQueueService,
            syncQueueService,
            syncDeviceIdentities,
            identity,
            authorization,
            runtime,
            interactiveSessions,
            unitOfWork);

        return new NetworkDeltaService(
            new FakeOutgoingDeltaBuilderService(),
            protocol,
            replay,
            payloadApplier,
            lifecycle,
            identity,
            unitOfWork,
            new FakeUserSnapshotInboxService(),
            new FakeUserSnapshotMergeCoordinator(),
            new FakeUserSyncKeyResolverService(),
            users,
            new FakeUserRevisionKnowledgeRepository(),
            interactiveSessions,
            new FakeUserControlOperationInboxService());
    }

    private static async Task<ValidDeltaSetup> CreateValidDeltaAsync()
    {
        var senderProvider = CreateIdentityProvider();
        var recipientProvider = CreateIdentityProvider();
        var sender = CreateIdentity(senderProvider);
        var recipient = CreateIdentity(recipientProvider);
        await sender.InitializeAsync();
        await sender.SetSyncOnAsync(true);
        await recipient.InitializeAsync();
        await recipient.SetSyncOnAsync(true);

        var target = new Device
        {
            Id = recipient.LocalDeviceId,
            PublicKey = recipient.AgreementPublicKey,
            SignPublicKey = recipient.SignPublicKey,
            TlsCertFingerprint = recipient.FingerprintHex,
            DeviceType = recipient.DeviceType,
            IsTrusted = true
        };
        var userDevices = new FakeUserDeviceRepository();
        var localUsers = new FakeLocalUserDeviceRepository();
        var builder = new OutgoingDeltaBuilderService(
            new InMemoryUserRepository(),
            new FakeGroupRepository(),
            new FakeDeviceRepository(),
            userDevices,
            new FakeSyncRouteRepository(userDevices, localUsers),
            sender,
            new FakeUserSnapshotPublisherService(sender),
            new FakeUserMembershipAuthorizationService());
        var delta = await builder.BuildAsync(new SyncItem
        {
            ModelId = Guid.NewGuid(),
            ModelType = SyncModelType.Group,
            ChangeType = SyncChangeType.Deleted
        }, target);

        return new ValidDeltaSetup(delta, recipient, senderProvider, recipientProvider);
    }

    private static ServiceProvider CreateIdentityProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDeviceIdentityRepository, FakeDeviceIdentityRepository>();
        services.AddSingleton<IUnitOfWork, FakeUnitOfWork>();
        services.AddSingleton<IKeyProtector, TestKeyProtector>();
        return services.BuildServiceProvider();
    }

    private static DeviceIdentityService CreateIdentity(IServiceProvider provider) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            () => DeviceType.WindowsPc);

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
