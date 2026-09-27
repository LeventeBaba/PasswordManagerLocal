using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync.Presence;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class DeviceOnlineStatusEvaluatorTests
{
    [TestMethod]
    public void InteractiveProfile_UsesConfiguredAuthenticatedPresenceBoundary()
    {
        var provider = CreateProvider(TimeSpan.FromSeconds(35), isInteractive: true);
        var registry = new RecordingDevicePresenceRegistry();
        var evaluator = new DeviceOnlineStatusEvaluator(provider, registry);

        evaluator.IsOnline("AA");

        Assert.AreEqual(TimeSpan.FromSeconds(35), registry.LastMaximumAge);
    }

    [TestMethod]
    public void BackgroundProfile_UsesConfiguredAuthenticatedPresenceBoundary()
    {
        var provider = CreateProvider(TimeSpan.FromSeconds(125), isInteractive: false);
        var registry = new RecordingDevicePresenceRegistry();
        var evaluator = new DeviceOnlineStatusEvaluator(provider, registry);

        evaluator.IsOnline("AA");

        Assert.AreEqual(TimeSpan.FromSeconds(125), registry.LastMaximumAge);
    }

    [TestMethod]
    public void ProfileChange_SubsequentQueryUsesNewTimeout()
    {
        var provider = CreateProvider(TimeSpan.FromSeconds(35), isInteractive: true);
        var registry = new RecordingDevicePresenceRegistry();
        var evaluator = new DeviceOnlineStatusEvaluator(provider, registry);
        evaluator.IsOnline("AA");

        provider.SetProfile(
            new BackendExecutionProfile(
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(125)),
            isInteractive: false);
        evaluator.IsOnline("AA");

        Assert.AreEqual(TimeSpan.FromSeconds(125), registry.LastMaximumAge);
    }

    [TestMethod]
    public void AuthenticatedPresenceBoundary_IsInclusiveAndThenExpires()
    {
        var now = DateTimeOffset.Parse("2026-07-24T08:00:00+00:00");
        var registry = new DevicePresenceRegistry(() => now);
        registry.RefreshAuthenticated("AABB", endpoint: null, DevicePresenceObservationSource.IncomingSync);
        var evaluator = new DeviceOnlineStatusEvaluator(
            CreateProvider(TimeSpan.FromSeconds(35), isInteractive: true),
            registry);

        now = now.AddSeconds(35);
        Assert.IsTrue(evaluator.IsOnline("AA:BB"));
        now = now.AddTicks(1);
        Assert.IsFalse(evaluator.IsOnline("AA:BB"));
    }

    [TestMethod]
    public void NoActiveProfile_IsOfflineWithoutRegistryQuery()
    {
        var provider = new FakeBackendExecutionProfileProvider();
        var registry = new RecordingDevicePresenceRegistry { IsOnlineResult = true };
        var evaluator = new DeviceOnlineStatusEvaluator(provider, registry);

        var online = evaluator.IsOnline("AA");

        Assert.IsFalse(online);
        Assert.IsNull(registry.LastMaximumAge);
    }

    private static FakeBackendExecutionProfileProvider CreateProvider(TimeSpan timeout, bool isInteractive)
    {
        var provider = new FakeBackendExecutionProfileProvider();
        provider.SetProfile(
            new BackendExecutionProfile(
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(15),
                timeout),
            isInteractive);
        return provider;
    }
}
