using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Services.Hosted;
using PasswordManagerLocal.Common.Backend.Sync.Presence;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
[DoNotParallelize]
public sealed class DevicePresenceEndToEndTests
{
    private const int TestTimeoutMilliseconds = 90_000;

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task InteractiveObserver_SeesBackgroundOnlyPeerAsOnlineWithoutMulticast()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await pair.Target.EnterBackgroundOnlyAsync();
        pair.Source.Presence.InvalidateAll("arrange direct-probe test");
        var remoteDevice = await pair.Source.CacheEndpointForAsync(pair.Target);

        var result = await pair.Source.ProbeAsync(remoteDevice, force: true);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(pair.Source.IsOnline(pair.Target));
        Assert.IsTrue(pair.Source.Presence.TryGetSnapshot(pair.Target.Identity.FingerprintHex, out var snapshot));
        Assert.AreEqual(DevicePresenceObservationSource.DirectProbe, snapshot!.LastObservationSource);
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task BackgroundOnlyObserver_DoesNotRunUiPresencePolling()
    {
        await using var pair = await CreateEnrolledPairAsync();
        var polling = pair.Target.Services.GetRequiredService<DevicePresencePollingHostedService>();

        await pair.Target.EnterBackgroundOnlyAsync();

        Assert.IsFalse(polling.IsRunning);
        var probeCalls = pair.Target.Transport.ProbeCalls;
        await polling.PollOnceAsync();
        Assert.AreEqual(probeCalls, pair.Target.Transport.ProbeCalls);
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task ReachablePeerWithWrongCertificate_RemainsOffline()
    {
        await using var pair = await CreateEnrolledPairAsync();
        var remoteDevice = await pair.Source.CacheEndpointForAsync(pair.Target);
        pair.Source.Presence.InvalidateAll("arrange wrong-certificate test");
        pair.Source.Transport.PresentedServerCertificateFingerprintOverride =
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        var result = await pair.Source.ProbeAsync(remoteDevice, force: true);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DevicePresenceFailureKind.TlsOrFingerprintMismatch, result.FailureKind);
        Assert.IsFalse(pair.Source.IsOnline(pair.Target));
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task UnreachablePeerEventuallyBecomesOfflineAndReturnsOnlineAfterListenerRestarts()
    {
        await using var pair = await CreateEnrolledPairAsync();
        var remoteDevice = await pair.Source.CacheEndpointForAsync(pair.Target);
        Assert.IsTrue((await pair.Source.ProbeAsync(remoteDevice, force: true)).IsSuccess);
        Assert.IsTrue(pair.Source.IsOnline(pair.Target));

        pair.Source.Transport.Disconnect();
        for (var attempt = 0; attempt < DevicePresenceRegistry.FailureThreshold; attempt++)
            Assert.IsFalse((await pair.Source.ProbeAsync(remoteDevice, force: true)).IsSuccess);

        Assert.IsFalse(pair.Source.IsOnline(pair.Target));

        pair.Source.ConnectTo(pair.Target);
        Assert.IsTrue((await pair.Source.ProbeAsync(remoteDevice, force: true)).IsSuccess);
        Assert.IsTrue(pair.Source.IsOnline(pair.Target));
    }

    [TestMethod]
    [Timeout(TestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task SuccessfulRegularSync_RefreshesAuthenticatedPresenceOnBothBackends()
    {
        await using var pair = await CreateEnrolledPairAsync();
        await pair.Source.Services.GetRequiredService<DevicePresencePollingHostedService>().StopAsync();
        await pair.Target.Services.GetRequiredService<DevicePresencePollingHostedService>().StopAsync();
        pair.Source.Presence.InvalidateAll("arrange regular-sync presence test");
        pair.Target.Presence.InvalidateAll("arrange regular-sync presence test");
        await pair.Source.PublishCurrentUserSnapshotAsync();

        var started = await pair.Source.StartSyncToAsync(pair.Target);
        Assert.IsTrue(started);
        await pair.Source.WaitForSyncIdleAsync(pair.Target.Identity.LocalDeviceId);

        Assert.IsTrue(pair.Source.IsOnline(pair.Target));
        Assert.IsTrue(pair.Target.IsOnline(pair.Source));
        Assert.IsTrue(pair.Source.Presence.TryGetSnapshot(pair.Target.Identity.FingerprintHex, out var outgoing));
        Assert.AreEqual(DevicePresenceObservationSource.OutgoingSync, outgoing!.LastObservationSource);
        Assert.IsTrue(pair.Target.Presence.TryGetSnapshot(pair.Source.Identity.FingerprintHex, out var incoming));
        Assert.AreEqual(DevicePresenceObservationSource.IncomingSync, incoming!.LastObservationSource);
    }

    private static async Task<PresencePair> CreateEnrolledPairAsync()
    {
        ProductionSyncTestHost? source = null;
        ProductionSyncTestHost? target = null;
        try
        {
            source = await ProductionSyncTestHost.CreateAsync();
            target = await ProductionSyncTestHost.CreateAsync();
            source.ConnectTo(target);
            target.ConnectTo(source);

            var username = $"presence{Guid.NewGuid().ToString("N")[..16]}";
            var sourceToken = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest(username));
            await source.EnableSyncAsync(sourceToken);
            var enrollment = await target.Endpoints.StartDeviceEnrollmentAsync();
            await source.Endpoints.AddDeviceByCodeAsync(sourceToken, enrollment.Code);
            await target.ActivateImportedSynchronizationAsync();

            await source.SyncTasks.StopAllAsync();
            await target.SyncTasks.StopAllAsync();
            await source.CacheEndpointForAsync(target);
            await target.CacheEndpointForAsync(source);

            var pair = new PresencePair(source, target);
            source = null;
            target = null;
            return pair;
        }
        catch
        {
            if (target is not null)
                await target.DisposeAsync();
            if (source is not null)
                await source.DisposeAsync();
            throw;
        }
    }

    private sealed class PresencePair : IAsyncDisposable
    {
        public PresencePair(ProductionSyncTestHost source, ProductionSyncTestHost target)
        {
            Source = source;
            Target = target;
        }

        public ProductionSyncTestHost Source { get; }
        public ProductionSyncTestHost Target { get; }

        public async ValueTask DisposeAsync()
        {
            Source.Transport.Disconnect();
            Target.Transport.Disconnect();
            await Target.DisposeAsync();
            await Source.DisposeAsync();
        }
    }
}
