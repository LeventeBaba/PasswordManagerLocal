using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Sync.Presence;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class DevicePresenceRegistryTests
{
    private const string Fingerprint = "AA:BB:CC";

    [TestMethod]
    public void AuthenticatedContact_MarksOnlineImmediately()
    {
        var registry = CreateRegistry(out _);

        registry.RefreshAuthenticated(Fingerprint, Endpoint("10.0.0.2"), DevicePresenceObservationSource.DirectProbe);

        Assert.IsTrue(registry.IsOnline(Fingerprint, TimeSpan.FromSeconds(35)));
        Assert.IsTrue(registry.TryGetSnapshot(Fingerprint, out var snapshot));
        Assert.AreEqual(DevicePresenceObservationSource.DirectProbe, snapshot!.LastObservationSource);
        Assert.AreEqual(0, snapshot.ConsecutiveFailures);
    }

    [TestMethod]
    public void PresenceExpiration_MarksOfflineWithoutDeletingEndpointCache()
    {
        var registry = CreateRegistry(out var clock);
        var endpoints = new DiscoveredDeviceEndpointRegistry(() => clock.Now);
        var endpoint = Endpoint("10.0.0.2");
        endpoints.AddOrUpdate(endpoint);
        registry.RefreshAuthenticated(Fingerprint, endpoint, DevicePresenceObservationSource.OutgoingSync);

        clock.Now = clock.Now.AddSeconds(36);

        Assert.IsFalse(registry.IsOnline(Fingerprint, TimeSpan.FromSeconds(35)));
        Assert.IsTrue(endpoints.TryGetByFingerprint(Fingerprint, out var cached));
        Assert.IsNotNull(cached);
    }

    [TestMethod]
    public void OneTransientFailure_DoesNotCauseOnlineFlicker()
    {
        var registry = CreateRegistry(out _);
        var endpoint = Endpoint("10.0.0.2");
        registry.RefreshAuthenticated(Fingerprint, endpoint, DevicePresenceObservationSource.DirectProbe);

        registry.RecordFailure(Fingerprint, endpoint, DevicePresenceFailureKind.Timeout);

        Assert.IsTrue(registry.IsOnline(Fingerprint, TimeSpan.FromSeconds(35)));
        Assert.IsTrue(registry.TryGetSnapshot(Fingerprint, out var snapshot));
        Assert.AreEqual(1, snapshot!.ConsecutiveFailures);
    }

    [TestMethod]
    public void AuthenticationMismatch_MarksPreviouslyOnlineDeviceOfflineImmediately()
    {
        var registry = CreateRegistry(out _);
        var endpoint = Endpoint("10.0.0.2");
        registry.RefreshAuthenticated(Fingerprint, endpoint, DevicePresenceObservationSource.DirectProbe);

        registry.RecordFailure(Fingerprint, endpoint, DevicePresenceFailureKind.TlsOrFingerprintMismatch);

        Assert.IsFalse(registry.IsOnline(Fingerprint, TimeSpan.FromSeconds(35)));
    }

    [TestMethod]
    public void RepeatedFailures_EventuallyMarkOffline()
    {
        var registry = CreateRegistry(out _);
        var endpoint = Endpoint("10.0.0.2");
        registry.RefreshAuthenticated(Fingerprint, endpoint, DevicePresenceObservationSource.DirectProbe);

        for (var index = 0; index < DevicePresenceRegistry.FailureThreshold; index++)
            registry.RecordFailure(Fingerprint, endpoint, DevicePresenceFailureKind.Unreachable);

        Assert.IsFalse(registry.IsOnline(Fingerprint, TimeSpan.FromSeconds(35)));
    }

    [TestMethod]
    public void SuccessfulLaterContact_RestoresOnlineAndClearsFailures()
    {
        var registry = CreateRegistry(out _);
        var endpoint = Endpoint("10.0.0.2");
        registry.RefreshAuthenticated(Fingerprint, endpoint, DevicePresenceObservationSource.DirectProbe);
        for (var index = 0; index < DevicePresenceRegistry.FailureThreshold; index++)
            registry.RecordFailure(Fingerprint, endpoint, DevicePresenceFailureKind.Unreachable);

        registry.RefreshAuthenticated(Fingerprint, endpoint, DevicePresenceObservationSource.IncomingSync);

        Assert.IsTrue(registry.IsOnline(Fingerprint, TimeSpan.FromSeconds(35)));
        Assert.IsTrue(registry.TryGetSnapshot(Fingerprint, out var snapshot));
        Assert.AreEqual(0, snapshot!.ConsecutiveFailures);
        Assert.AreEqual(DevicePresenceObservationSource.IncomingSync, snapshot.LastObservationSource);
    }

    [TestMethod]
    public void RepeatedAuthenticatedIntervals_KeepOnlineStateStable()
    {
        var registry = CreateRegistry(out var clock);
        var endpoint = Endpoint("10.0.0.2");
        registry.RefreshAuthenticated(Fingerprint, endpoint, DevicePresenceObservationSource.DirectProbe);

        for (var interval = 0; interval < 4; interval++)
        {
            clock.Now = clock.Now.AddSeconds(15);
            Assert.IsTrue(registry.IsOnline(Fingerprint, TimeSpan.FromSeconds(35)));
            registry.RefreshAuthenticated(Fingerprint, endpoint, DevicePresenceObservationSource.DirectProbe);
        }

        Assert.IsTrue(registry.IsOnline(Fingerprint, TimeSpan.FromSeconds(35)));
    }

    [TestMethod]
    public void EndpointChange_InvalidatesPresenceUntilReauthenticated()
    {
        var registry = CreateRegistry(out _);
        registry.RefreshAuthenticated(Fingerprint, Endpoint("10.0.0.2"), DevicePresenceObservationSource.DirectProbe);

        registry.HandleEndpointChanged(Fingerprint, Endpoint("10.0.1.2"));

        Assert.IsFalse(registry.IsOnline(Fingerprint, TimeSpan.FromSeconds(35)));
    }

    [TestMethod]
    public void NetworkReset_InvalidatesAllPresence()
    {
        var registry = CreateRegistry(out _);
        registry.RefreshAuthenticated(Fingerprint, Endpoint("10.0.0.2"), DevicePresenceObservationSource.DirectProbe);

        registry.InvalidateAll("test network reset");

        Assert.IsFalse(registry.IsOnline(Fingerprint, TimeSpan.FromSeconds(35)));
    }

    private static DevicePresenceRegistry CreateRegistry(out MutableClock clock)
    {
        var mutableClock = new MutableClock
        {
            Now = DateTimeOffset.Parse("2026-07-24T08:00:00+00:00")
        };
        clock = mutableClock;
        return new DevicePresenceRegistry(() => mutableClock.Now);
    }

    private static DiscoveredDeviceEndpoint Endpoint(string host) => new()
    {
        Host = host,
        Port = 26688,
        TlsCertFingerprint = Fingerprint
    };

    private sealed class MutableClock
    {
        public DateTimeOffset Now { get; set; }
    }
}
