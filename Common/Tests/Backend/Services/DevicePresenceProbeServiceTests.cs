using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Services.Hosted;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Sync.Presence;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class DevicePresenceProbeServiceTests
{
    [TestMethod]
    public async Task AuthenticatedProbe_MarksExpectedDeviceOnline()
    {
        using var fixture = CreateFixture(isInteractive: true);
        fixture.Endpoints.AddOrUpdate(fixture.Endpoint);

        var result = await fixture.Service.ProbeAsync(fixture.Device, force: true);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(fixture.Presence.IsOnline(fixture.Device.TlsCertFingerprint, TimeSpan.FromSeconds(35)));
        Assert.AreEqual(1, fixture.Transport.ProbeCalls);
    }

    [TestMethod]
    public async Task FailedTlsValidation_DoesNotMarkDeviceOnline()
    {
        using var fixture = CreateFixture(isInteractive: true);
        fixture.Endpoints.AddOrUpdate(fixture.Endpoint);
        fixture.Transport.ProbeResult = DevicePresenceProbeResult.Failed(DevicePresenceFailureKind.TlsOrFingerprintMismatch);

        var result = await fixture.Service.ProbeAsync(fixture.Device, force: true);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsFalse(fixture.Presence.IsOnline(fixture.Device.TlsCertFingerprint, TimeSpan.FromSeconds(35)));
    }

    [TestMethod]
    public async Task UnknownOrUnauthorizedDevice_IsNotProbedOrMarkedOnline()
    {
        using var fixture = CreateFixture(isInteractive: true);
        fixture.Endpoints.AddOrUpdate(fixture.Endpoint);
        fixture.Presence.RefreshAuthenticated(
            fixture.Device.TlsCertFingerprint,
            fixture.Endpoint,
            DevicePresenceObservationSource.OutgoingSync);
        Assert.IsTrue(fixture.Presence.IsOnline(fixture.Device.TlsCertFingerprint, TimeSpan.FromSeconds(35)));
        fixture.Device.IsTrusted = false;

        var result = await fixture.Service.ProbeAsync(fixture.Device, force: true);

        Assert.AreEqual(DevicePresenceFailureKind.Unauthorized, result.FailureKind);
        Assert.AreEqual(0, fixture.Transport.ProbeCalls);
        Assert.IsFalse(fixture.Presence.IsOnline(fixture.Device.TlsCertFingerprint, TimeSpan.FromSeconds(35)));
    }

    [TestMethod]
    public async Task BackgroundOnlyMode_DoesNotRunUiFacingProbe()
    {
        using var fixture = CreateFixture(isInteractive: false);
        fixture.Endpoints.AddOrUpdate(fixture.Endpoint);

        var result = await fixture.Service.ProbeAsync(fixture.Device, force: true);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(0, fixture.Transport.ProbeCalls);
    }

    [TestMethod]
    public async Task EndpointChange_InvalidatesOldPresenceAndSuccessfulReprobeRestoresIt()
    {
        using var fixture = CreateFixture(isInteractive: true);
        fixture.Endpoints.AddOrUpdate(fixture.Endpoint);
        await fixture.Service.ProbeAsync(fixture.Device, force: true);
        Assert.IsTrue(fixture.Presence.IsOnline(fixture.Device.TlsCertFingerprint, TimeSpan.FromSeconds(35)));

        var changed = new DiscoveredDeviceEndpoint
        {
            Host = "10.0.1.25",
            Port = fixture.Endpoint.Port,
            TlsCertFingerprint = fixture.Endpoint.TlsCertFingerprint
        };
        fixture.Endpoints.AddOrUpdate(changed);
        fixture.Presence.HandleEndpointChanged(fixture.Device.TlsCertFingerprint, changed);

        Assert.IsFalse(fixture.Presence.IsOnline(fixture.Device.TlsCertFingerprint, TimeSpan.FromSeconds(35)));
        await fixture.Service.ProbeAsync(fixture.Device, force: true);
        Assert.IsTrue(fixture.Presence.IsOnline(fixture.Device.TlsCertFingerprint, TimeSpan.FromSeconds(35)));
    }

    [TestMethod]
    public async Task InteractivePolling_StartsAndStopsWhileBackgroundPollIsSkipped()
    {
        using var host = new BackendTestHost();
        var profiles = (FakeBackendExecutionProfileProvider)host.Services.GetRequiredService<IBackendExecutionProfileProvider>();
        var probes = host.Services.GetRequiredService<IDevicePresenceProbeService>();
        var presence = host.Services.GetRequiredService<IDevicePresenceRegistry>();
        var scopeFactory = host.Services.GetRequiredService<IServiceScopeFactory>();
        using var polling = new DevicePresencePollingHostedService(scopeFactory, profiles, probes, presence);

        await polling.StartAsync();
        Assert.IsTrue(polling.IsRunning);
        await polling.StopAsync();
        Assert.IsFalse(polling.IsRunning);

        profiles.SetProfile(
            new BackendExecutionProfile(
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(125)),
            isInteractive: false);
        var transport = host.Services.GetRequiredService<FakeSyncTransportClientService>();
        await polling.PollOnceAsync();
        Assert.AreEqual(0, transport.ProbeCalls);
    }

    private static Fixture CreateFixture(bool isInteractive)
    {
        var identity = new FakeDeviceIdentityService
        {
            IsSyncOn = true,
            LocalDeviceId = Guid.NewGuid(),
            SignPublicKey = RandomNumberGenerator.GetBytes(32),
            FingerprintHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
        };
        var profiles = new FakeBackendExecutionProfileProvider();
        profiles.SetProfile(
            new BackendExecutionProfile(
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(35)),
            isInteractive);
        var endpoints = new DiscoveredDeviceEndpointRegistry();
        var presence = new DevicePresenceRegistry(TimeProvider.System);
        var transport = new FakeSyncTransportClientService();
        var routeServices = new ServiceCollection();
        routeServices.AddScoped<ISyncRouteRepository, AlwaysEligibleSyncRouteRepository>();
        var routeProvider = routeServices.BuildServiceProvider();
        var service = new DevicePresenceProbeService(
            identity,
            profiles,
            endpoints,
            presence,
            transport,
            routeProvider.GetRequiredService<IServiceScopeFactory>());
        var device = new Device
        {
            Id = Guid.NewGuid(),
            PublicKey = RandomNumberGenerator.GetBytes(32),
            SignPublicKey = RandomNumberGenerator.GetBytes(32),
            TlsCertFingerprint = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            IsTrusted = true,
            IsBlocked = false,
            DeviceType = DeviceType.AndroidMobile
        };
        var endpoint = new DiscoveredDeviceEndpoint
        {
            Host = "10.0.0.25",
            Port = 26688,
            TlsCertFingerprint = device.TlsCertFingerprint
        };
        return new Fixture(service, endpoints, presence, transport, device, endpoint, routeProvider);
    }

    private sealed record Fixture(
        DevicePresenceProbeService Service,
        IDiscoveredDeviceEndpointRegistry Endpoints,
        IDevicePresenceRegistry Presence,
        FakeSyncTransportClientService Transport,
        Device Device,
        DiscoveredDeviceEndpoint Endpoint,
        ServiceProvider RouteProvider) : IDisposable
    {
        public void Dispose()
        {
            Service.Dispose();
            RouteProvider.Dispose();
        }
    }

    private sealed class AlwaysEligibleSyncRouteRepository : ISyncRouteRepository
    {
        public Task<bool> IsEligibleAsync(Guid userId, Guid remoteDeviceId, CancellationToken ct = default) =>
            Task.FromResult(userId != Guid.Empty && remoteDeviceId != Guid.Empty);

        public Task<bool> HasAnyEligibleAsync(IReadOnlyCollection<Guid> userIds, Guid remoteDeviceId, CancellationToken ct = default) =>
            Task.FromResult(userIds.Count != 0 && remoteDeviceId != Guid.Empty);

        public Task<IReadOnlyList<Guid>> ListEligibleUserIdsAsync(IReadOnlyCollection<Guid> userIds, Guid remoteDeviceId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(remoteDeviceId == Guid.Empty ? [] : userIds.Where(id => id != Guid.Empty).Distinct().ToArray());

        public Task<IReadOnlyList<Guid>> ListAllEligibleUserIdsAsync(Guid remoteDeviceId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(remoteDeviceId == Guid.Empty ? [] : [Guid.NewGuid()]);

        public Task<bool> HasEligibleUserForDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
            Task.FromResult(deviceId != Guid.Empty);
    }
}
