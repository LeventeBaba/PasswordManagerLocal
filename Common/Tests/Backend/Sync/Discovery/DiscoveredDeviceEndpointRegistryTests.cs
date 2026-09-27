using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Sync.Discovery;

[TestClass]
public sealed class DiscoveredDeviceEndpointRegistryTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void AddOrUpdate_NormalizesFingerprint_AndReturnsIndependentCopies()
    {
        var registry = new DiscoveredDeviceEndpointRegistry();
        var source = new DiscoveredDeviceEndpoint
        {
            Host = "192.168.1.25",
            Port = 26688,
            TlsCertFingerprint = "aa:bb:cc"
        };

        registry.AddOrUpdate(source);

        MSTestAssert.IsTrue(registry.TryGetByFingerprint("AABBCC", out var first));
        MSTestAssert.IsNotNull(first);
        MSTestAssert.AreEqual("192.168.1.25", first.Host);
        MSTestAssert.AreNotSame(source, first);

        MSTestAssert.IsTrue(registry.TryGetByFingerprint("aa bb cc", out var second));
        MSTestAssert.IsNotNull(second);
        MSTestAssert.AreEqual("192.168.1.25", second.Host);
        MSTestAssert.AreNotSame(first, second);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void AddOrUpdate_InvalidEndpoint_IsIgnored()
    {
        var registry = new DiscoveredDeviceEndpointRegistry();

        registry.AddOrUpdate(new DiscoveredDeviceEndpoint
        {
            Host = " ",
            Port = 26688,
            TlsCertFingerprint = "AABB"
        });
        registry.AddOrUpdate(new DiscoveredDeviceEndpoint
        {
            Host = "192.168.1.20",
            Port = 0,
            TlsCertFingerprint = "AABB"
        });
        registry.AddOrUpdate(new DiscoveredDeviceEndpoint
        {
            Host = "192.168.1.20",
            Port = 26688,
            TlsCertFingerprint = " "
        });

        MSTestAssert.IsFalse(registry.TryGetByFingerprint("AABB", out _));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void AddOrUpdate_SameFingerprint_ReplacesEndpoint()
    {
        var registry = new DiscoveredDeviceEndpointRegistry();

        registry.AddOrUpdate(new DiscoveredDeviceEndpoint
        {
            Host = "192.168.1.20",
            Port = 26688,
            TlsCertFingerprint = "AABB"
        });
        registry.AddOrUpdate(new DiscoveredDeviceEndpoint
        {
            Host = "192.168.1.21",
            Port = 30000,
            TlsCertFingerprint = "aa:bb"
        });

        MSTestAssert.IsTrue(registry.TryGetByFingerprint("AABB", out var endpoint));
        MSTestAssert.IsNotNull(endpoint);
        MSTestAssert.AreEqual("192.168.1.21", endpoint.Host);
        MSTestAssert.AreEqual(30000, endpoint.Port);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void RemoveAndClear_RemoveStoredEndpoints()
    {
        var registry = new DiscoveredDeviceEndpointRegistry();
        registry.AddOrUpdate(new DiscoveredDeviceEndpoint { Host = "host-a", Port = 1, TlsCertFingerprint = "AA" });
        registry.AddOrUpdate(new DiscoveredDeviceEndpoint { Host = "host-b", Port = 2, TlsCertFingerprint = "BB" });

        MSTestAssert.IsTrue(registry.TryRemove("aa"));
        MSTestAssert.IsFalse(registry.TryGetByFingerprint("AA", out _));
        MSTestAssert.IsFalse(registry.TryRemove("AA"));

        registry.Clear();
        MSTestAssert.IsFalse(registry.TryGetByFingerprint("BB", out _));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void IsRecentlyDiscovered_UsesObservationTimeAndMaximumAge()
    {
        var now = new DateTimeOffset(2026, 7, 13, 18, 0, 0, TimeSpan.Zero);
        var registry = new DiscoveredDeviceEndpointRegistry(() => now);
        registry.AddOrUpdate(new DiscoveredDeviceEndpoint
        {
            Host = "192.168.1.25",
            Port = 26688,
            TlsCertFingerprint = "AABB"
        });

        MSTestAssert.IsTrue(registry.IsRecentlyDiscovered("aa:bb", TimeSpan.FromSeconds(75)));

        now = now.AddSeconds(76);

        MSTestAssert.IsFalse(registry.IsRecentlyDiscovered("AABB", TimeSpan.FromSeconds(75)));
        MSTestAssert.IsTrue(registry.TryGetByFingerprint("AABB", out _));
    }
}
