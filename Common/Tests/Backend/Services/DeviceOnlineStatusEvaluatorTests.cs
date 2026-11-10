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
    public void ProfileChange_AppliesTheNewTimeoutToExistingAuthenticatedPresence()
    {
        var now = DateTimeOffset.Parse("2026-07-24T08:00:00+00:00");
        var registry = new DevicePresenceRegistry(() => now);
        registry.RefreshAuthenticated("AABB", endpoint: null, DevicePresenceObservationSource.IncomingSync);
        var provider = CreateProvider(TimeSpan.FromSeconds(35), isInteractive: true);
        var evaluator = new DeviceOnlineStatusEvaluator(provider, registry);

        now = now.AddSeconds(35);
        Assert.IsTrue(evaluator.IsOnline("AA:BB"));
        now = now.AddTicks(1);
        Assert.IsFalse(evaluator.IsOnline("AA:BB"));

        var refreshedAt = now;
        registry.RefreshAuthenticated("AABB", endpoint: null, DevicePresenceObservationSource.IncomingSync);
        now = refreshedAt.AddSeconds(36);
        provider.SetProfile(
            new BackendExecutionProfile(
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(125)),
            isInteractive: false);

        Assert.IsTrue(evaluator.IsOnline("AA:BB"));
        now = refreshedAt.AddSeconds(125);
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
