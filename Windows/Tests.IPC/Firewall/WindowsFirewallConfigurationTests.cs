using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Sync;
using PasswordManagerLocal.Windows.Frontend.Firewall;

namespace PasswordManagerLocal.Windows.Tests.IPC.Firewall;

[TestClass]
public sealed class WindowsFirewallConfigurationTests
{
    private const string Agent = @"C:\Apps\PasswordManagerLocalDemo\PasswordManagerLocal.Windows.Agent.exe";

    [TestMethod]
    public void DefaultPolicyAddsOnlyAgentAndPortScopedInboundRulesAndIsIdempotent()
    {
        using var policy = new FakePolicy();
        var service = new WindowsFirewallConfigurationService(policy, Agent);
        Assert.IsFalse(service.Check().IsConfigured);
        Assert.IsTrue(service.Configure().IsConfigured);
        Assert.AreEqual(2, policy.Rules.Count);
        Assert.IsTrue(policy.Rules.All(rule => rule.Program == Agent && rule.Direction == 1 &&
            rule.Profiles == 7 && rule.Action == 1 && rule.Enabled && !rule.EdgeTraversal));
        CollectionAssert.AreEquivalent(new[] { SyncProtocolDefaults.TcpPort.ToString(), SyncProtocolDefaults.DiscoveryUdpPort.ToString() },
            policy.Rules.Select(rule => rule.LocalPorts).ToArray());
        Assert.IsTrue(service.Configure().IsConfigured);
        Assert.AreEqual(2, policy.Writes);
        Assert.AreEqual(0, policy.Removes);
    }

    [TestMethod]
    public void OutboundBlockRulesCoverOnlyRestrictedProfilesAndAllowDiscoveryMulticast()
    {
        using var policy = new FakePolicy();
        policy.Profiles[2] = policy.Profiles[2] with { DefaultOutboundAction = 0 };
        var service = new WindowsFirewallConfigurationService(policy, Agent);
        Assert.IsTrue(service.Configure().IsConfigured);
        var outbound = policy.Rules.Where(rule => rule.Direction == 2).ToArray();
        Assert.AreEqual(2, outbound.Length);
        Assert.IsTrue(outbound.All(rule => rule.Profiles == 4 && rule.Program == Agent));
        var tcp = outbound.Single(rule => rule.Protocol == 6);
        Assert.AreEqual("*", tcp.LocalPorts);
        Assert.AreEqual(SyncProtocolDefaults.TcpPort.ToString(), tcp.RemotePorts);
        var udp = outbound.Single(rule => rule.Protocol == 17);
        Assert.AreEqual(SyncProtocolDefaults.DiscoveryUdpPort.ToString(), udp.LocalPorts);
        Assert.AreEqual(udp.LocalPorts, udp.RemotePorts);
        // '*' includes the multicast group; LocalSubnet alone would exclude it.
        Assert.AreEqual("*", udp.RemoteAddresses);
    }

    [TestMethod]
    public void ReturningToOutboundAllowRemovesNowUnnecessaryOwnedOutboundRules()
    {
        using var policy = new FakePolicy();
        policy.Profiles[1] = policy.Profiles[1] with { DefaultOutboundAction = 0 };
        var service = new WindowsFirewallConfigurationService(policy, Agent);
        Assert.IsTrue(service.Configure().IsConfigured);
        Assert.AreEqual(4, policy.Rules.Count);
        policy.Profiles[1] = policy.Profiles[1] with { DefaultOutboundAction = 1 };
        Assert.IsFalse(service.Check().IsConfigured);
        Assert.IsTrue(service.Configure().IsConfigured);
        Assert.AreEqual(2, policy.Rules.Count);
        Assert.AreEqual(2, policy.Removes);
    }

    [TestMethod]
    public void ChangedAgentPathAndTamperedFiltersAreRepairedInsteadOfTrustingRuleNames()
    {
        using var policy = new FakePolicy();
        var service = new WindowsFirewallConfigurationService(policy, Agent);
        Assert.IsTrue(service.Configure().IsConfigured);
        policy.Rules[0] = policy.Rules[0] with { Program = @"C:\Old\PasswordManagerLocal.Windows.Agent.exe" };
        policy.Rules[1] = policy.Rules[1] with { LocalPorts = "5353", Profiles = 2 };
        Assert.IsFalse(service.Check().IsConfigured);
        Assert.IsTrue(service.Configure().IsConfigured);
        Assert.AreEqual(4, policy.Writes);
        Assert.AreEqual(2, policy.Rules.Count);
    }

    [TestMethod]
    public void DuplicateFriendlyNamesAreReplacedWithOneCorrectRule()
    {
        using var policy = new FakePolicy();
        var service = new WindowsFirewallConfigurationService(policy, Agent);
        Assert.IsTrue(service.Configure().IsConfigured);
        policy.Rules.Add(policy.Rules[0] with { LocalPorts = "*" });
        Assert.IsFalse(service.Check().IsConfigured);
        Assert.IsTrue(service.Configure().IsConfigured);
        Assert.AreEqual(2, policy.Rules.Count);
        Assert.AreEqual(1, policy.Rules.Count(rule => rule.Protocol == 6));
    }

    [TestMethod]
    public void SeveralActiveProfilesCannotBeSatisfiedByAnExceptionForOnlyOneProfile()
    {
        using var policy = new FakePolicy { CurrentProfiles = 6 };
        var service = new WindowsFirewallConfigurationService(policy, Agent);
        Assert.IsTrue(service.Configure().IsConfigured);
        policy.Rules[0] = policy.Rules[0] with { Profiles = 2 };
        Assert.IsFalse(service.Check().IsConfigured);
    }

    [TestMethod]
    public void ConfiguringDemoFirewallPreservesFullEditionAndLegacyRules()
    {
        using var policy = new FakePolicy();
        const string fullGroup = "PasswordManagerLocal.LocalNetwork.v2";
        const string fullAgent = @"C:\Apps\PasswordManagerLocal\PasswordManagerLocal.Windows.Agent.exe";
        var fullRule = new WindowsFirewallRule(fullGroup + ".Tcp.In", fullGroup, fullAgent, 6, 1, 1, true, 7, "26688", "*");
        var legacyRule = new WindowsFirewallRule("PasswordManagerLocal Sync TCP Port", "", "", 6, 1, 1, true, 7, "26688", "*");
        policy.Rules.AddRange([fullRule, legacyRule]);

        var service = new WindowsFirewallConfigurationService(policy, Agent);
        Assert.IsTrue(service.Configure().IsConfigured);
        Assert.AreEqual(4, policy.Rules.Count);
        Assert.IsTrue(policy.Rules.Contains(fullRule));
        Assert.IsTrue(policy.Rules.Contains(legacyRule));
        Assert.AreEqual(0, policy.Removes);
    }

    [TestMethod]
    public void AnExplicitAgentBlockIsReportedAndNeverRemoved()
    {
        using var policy = new FakePolicy();
        policy.Rules.Add(BlockRule("Administrator block", Agent));
        var result = new WindowsFirewallConfigurationService(policy, Agent).Configure();
        Assert.IsFalse(result.IsConfigured);
        Assert.IsFalse(result.CanRequestPermission);
        StringAssert.Contains(result.Details!, "Administrator block");
        Assert.AreEqual(0, policy.Writes);
        Assert.AreEqual(0, policy.Removes);
        Assert.AreEqual(1, policy.Rules.Count);
    }

    [TestMethod]
    public void AllApplicationPortRangeBlocksAreAlsoReported()
    {
        using var policy = new FakePolicy();
        policy.Rules.Add(BlockRule("Port restriction", "*") with { LocalPorts = "26680-26690" });
        var result = new WindowsFirewallConfigurationService(policy, Agent).Check();
        Assert.IsFalse(result.CanRequestPermission);
        StringAssert.Contains(result.Details!, "Port restriction");
    }

    [TestMethod]
    public void UnrelatedPortsAndServiceOnlyBlocksDoNotPreventSetup()
    {
        using var policy = new FakePolicy();
        policy.Rules.Add(BlockRule("HTTPS restriction", "*") with { LocalPorts = "443" });
        policy.Rules.Add(BlockRule("Service restriction", "*") with { ServiceName = "SomeWindowsService" });
        Assert.IsTrue(new WindowsFirewallConfigurationService(policy, Agent).Configure().IsConfigured);
        Assert.AreEqual(4, policy.Rules.Count);
        Assert.AreEqual(0, policy.Removes);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void ManagedPolicyRestrictionsPreventChangesAndRepeatedElevation(int modifyState)
    {
        using var policy = new FakePolicy { ModifyState = modifyState };
        var result = new WindowsFirewallConfigurationService(policy, Agent).Configure();
        Assert.IsFalse(result.IsConfigured);
        Assert.IsFalse(result.CanRequestPermission);
        Assert.AreEqual(0, policy.Writes);
    }

    [TestMethod]
    public void BlockAllIncomingConnectionsPreventsChanges()
    {
        using var policy = new FakePolicy();
        policy.Profiles[1] = policy.Profiles[1] with { BlockAllInboundTraffic = true };
        var result = new WindowsFirewallConfigurationService(policy, Agent).Configure();
        Assert.IsFalse(result.CanRequestPermission);
        Assert.AreEqual(0, policy.Writes);
    }

    [TestMethod]
    public void DisabledFirewallDoesNotCauseTheAppToChangeWindowsPolicy()
    {
        using var policy = new FakePolicy();
        policy.Profiles[1] = policy.Profiles[1] with { Enabled = false };
        Assert.IsTrue(new WindowsFirewallConfigurationService(policy, Agent).Configure().IsConfigured);
        Assert.AreEqual(0, policy.Writes);
    }

    [TestMethod]
    public void FailedConfigurationCanBeRetriedWithoutRemovingFullEditionRules()
    {
        using var policy = new FakePolicy { FailAtWrite = 2 };
        policy.Rules.Add(new("PasswordManagerLocal Sync TCP Port", "", "", 6, 1, 1, true, 7, "26688", "*"));
        var service = new WindowsFirewallConfigurationService(policy, Agent);
        Assert.ThrowsExactly<IOException>(() => service.Configure());
        Assert.AreEqual(0, policy.Removes);
        Assert.IsTrue(policy.Rules.Any(rule => rule.Name == "PasswordManagerLocal Sync TCP Port"));
        policy.FailAtWrite = null;
        Assert.IsTrue(service.Configure().IsConfigured);
        Assert.AreEqual(3, policy.Rules.Count);
        Assert.IsTrue(policy.Rules.Any(rule => rule.Name == "PasswordManagerLocal Sync TCP Port"));
    }

    private static WindowsFirewallRule BlockRule(string name, string program) =>
        new(name, "", program, 6, 1, 0, true, 7, "26688", "*");

    private sealed class FakePolicy : IWindowsFirewallPolicy
    {
        internal int CurrentProfiles { get; init; } = 2;
        internal int ModifyState { get; init; }
        internal List<WindowsFirewallProfile> Profiles { get; } =
            [new(1, true, false, 1), new(2, true, false, 1), new(4, true, false, 1)];
        internal List<WindowsFirewallRule> Rules { get; } = [];
        internal int Writes { get; private set; }
        internal int Removes { get; private set; }
        internal int? FailAtWrite { get; set; }
        public WindowsFirewallSnapshot Read() => new(CurrentProfiles, ModifyState, Profiles.ToArray(), Rules.ToArray());
        public void Add(WindowsFirewallRule rule)
        {
            Writes++;
            if (Writes == FailAtWrite)
                throw new IOException("Simulated firewall write failure.");
            Rules.Add(rule);
        }
        public void Remove(string name)
        {
            Removes++;
            Rules.RemoveAll(rule => rule.Name == name);
        }
        public void Dispose() { }
    }
}
