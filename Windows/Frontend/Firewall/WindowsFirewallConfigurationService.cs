using System.Globalization;
using PasswordManagerLocal.Common.Contracts.Sync;
using PasswordManagerLocal.Common.Frontend.Services;

namespace PasswordManagerLocal.Windows.Frontend.Firewall;

internal sealed class WindowsFirewallConfigurationService(IWindowsFirewallPolicy policy, string agentPath)
{
    internal const string RuleGroup = "PasswordManagerLocalDemo.LocalNetwork.v2";
    internal const int AllProfiles = 7;
    internal const int Inbound = 1;
    internal const int Outbound = 2;
    internal const int Block = 0;
    internal const int Allow = 1;
    internal const int Tcp = 6;
    internal const int Udp = 17;

    internal static readonly string[] ManagedRuleNames =
    [
        RuleGroup + ".Tcp.In", RuleGroup + ".Udp.In",
        RuleGroup + ".Tcp.Out", RuleGroup + ".Udp.Out"
    ];

    internal IReadOnlyList<WindowsFirewallRule> RequiredRules(WindowsFirewallSnapshot snapshot)
    {
        var tcpPort = SyncProtocolDefaults.TcpPort.ToString(CultureInfo.InvariantCulture);
        var udpPort = SyncProtocolDefaults.DiscoveryUdpPort.ToString(CultureInfo.InvariantCulture);
        var rules = new List<WindowsFirewallRule>
        {
            new(ManagedRuleNames[0], RuleGroup, agentPath, Tcp, Inbound, Allow, true, AllProfiles, tcpPort, "*"),
            new(ManagedRuleNames[1], RuleGroup, agentPath, Udp, Inbound, Allow, true, AllProfiles, udpPort, "*")
        };
        // Cover inactive profiles too, so a later network change does not require
        // another prompt. The normal Windows outbound-allow policy needs no rule.
        var outboundProfiles = snapshot.Profiles
            .Where(profile => profile.Enabled && profile.DefaultOutboundAction == Block)
            .Aggregate(0, (profiles, profile) => profiles | profile.Type);
        if (outboundProfiles != 0)
        {
            rules.Add(new(ManagedRuleNames[2], RuleGroup, agentPath, Tcp, Outbound, Allow, true,
                outboundProfiles, "*", tcpPort));
            rules.Add(new(ManagedRuleNames[3], RuleGroup, agentPath, Udp, Outbound, Allow, true,
                outboundProfiles, udpPort, udpPort));
        }
        return rules;
    }

    internal FirewallPermissionCheckResult Check() => Check(policy.Read());

    internal FirewallPermissionCheckResult Configure()
    {
        var snapshot = policy.Read();
        var check = Check(snapshot);
        if (!check.CanRequestPermission)
            return check;

        var required = RequiredRules(snapshot);
        foreach (var rule in required)
        {
            var existing = snapshot.Rules.Where(candidate =>
                candidate.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (existing.Length == 1 && Equivalent(existing[0], rule))
                continue;
            // COM Name is a friendly name, not a PowerShell rule identifier.
            // Explicit replacement also handles duplicate friendly names.
            foreach (var previous in existing)
                policy.Remove(previous.Name);
            policy.Add(rule);
        }

        // Retire only obsolete rules owned by this edition.
        var obsolete = ManagedRuleNames.Except(required.Select(rule => rule.Name));
        foreach (var name in obsolete)
        {
            foreach (var rule in snapshot.Rules.Where(rule =>
                         rule.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                policy.Remove(rule.Name);
        }
        return Check();
    }

    private FirewallPermissionCheckResult Check(WindowsFirewallSnapshot snapshot)
    {
        var active = snapshot.Profiles
            .Where(profile => profile.Enabled && (snapshot.CurrentProfiles & profile.Type) != 0)
            .ToArray();
        if (active.Length == 0)
            return Result(true, false, null);
        if (snapshot.LocalPolicyModifyState != 0 || active.Any(profile => profile.BlockAllInboundTraffic))
            return Result(false, false,
                "Windows policy prevents local firewall exceptions or blocks all incoming connections. " +
                "Change that policy in Windows Firewall or contact your administrator, then try again.");

        var blockingRule = snapshot.Rules.FirstOrDefault(rule => IsRelevantBlock(rule, active));
        if (blockingRule is not null)
            return Result(false, false,
                $"The firewall block rule '{blockingRule.Name}' may block the Agent's synchronization or discovery traffic. " +
                "Review that rule in Windows Firewall, then try again. The app will not remove it automatically.");

        var required = RequiredRules(snapshot);
        var complete = required.All(expected =>
                snapshot.Rules.Count(existing => existing.Name.Equals(expected.Name, StringComparison.OrdinalIgnoreCase)) == 1 &&
                snapshot.Rules.Any(existing => Equivalent(existing, expected))) &&
            !snapshot.Rules.Any(rule => ManagedRuleNames.Contains(rule.Name, StringComparer.OrdinalIgnoreCase) &&
                !required.Any(expected => expected.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase)));
        return Result(complete, !complete, complete ? null :
            "The Agent needs application-specific firewall rules for TCP synchronization and UDP discovery.");
    }

    private bool IsRelevantBlock(WindowsFirewallRule rule, WindowsFirewallProfile[] active)
    {
        if (!rule.Enabled || rule.Action != Block || !active.Any(profile => (rule.Profiles & profile.Type) != 0) ||
            (!IsAny(rule.Program) && !SamePath(rule.Program, agentPath)) ||
            !IsAny(rule.ServiceName))
            return false;
        if (rule.Direction is not (Inbound or Outbound))
            return false;
        var localPorts = rule.LocalPorts;
        var remotePorts = rule.RemotePorts;
        return ((rule.Protocol is Tcp or 256) &&
                (rule.Direction == Inbound ? IncludesPort(localPorts, SyncProtocolDefaults.TcpPort)
                    : IncludesPort(remotePorts, SyncProtocolDefaults.TcpPort))) ||
            ((rule.Protocol is Udp or 256) && IncludesPort(localPorts, SyncProtocolDefaults.DiscoveryUdpPort) &&
                (rule.Direction == Inbound || IncludesPort(remotePorts, SyncProtocolDefaults.DiscoveryUdpPort)));
    }

    private static bool Equivalent(WindowsFirewallRule actual, WindowsFirewallRule expected) =>
        actual.Name.Equals(expected.Name, StringComparison.OrdinalIgnoreCase) &&
        actual.Grouping == expected.Grouping && SamePath(actual.Program, expected.Program) &&
        actual.Protocol == expected.Protocol && actual.Direction == expected.Direction &&
        actual.Action == Allow && actual.Enabled && actual.Profiles == expected.Profiles &&
        SameFilter(actual.LocalPorts, expected.LocalPorts) && SameFilter(actual.RemotePorts, expected.RemotePorts) &&
        SameFilter(actual.RemoteAddresses, expected.RemoteAddresses) && IsAny(actual.LocalAddresses) &&
        IsAny(actual.ServiceName) && actual.InterfaceTypes.Equals("All", StringComparison.OrdinalIgnoreCase) &&
        !actual.EdgeTraversal;

    private static bool SameFilter(string actual, string expected) =>
        (IsAny(actual) && IsAny(expected)) || actual.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsAny(string value) => string.IsNullOrWhiteSpace(value) || value == "*" || value.Equals("Any", StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string left, string right)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(left)).Equals(
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(right)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IncludesPort(string filter, int port)
    {
        if (IsAny(filter))
            return true;
        foreach (var token in filter.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, out var single) && single == port)
                return true;
            var range = token.Split('-');
            if (range.Length == 2 && int.TryParse(range[0], out var first) && int.TryParse(range[1], out var last) &&
                port >= first && port <= last)
                return true;
        }
        return false;
    }

    private static FirewallPermissionCheckResult Result(bool configured, bool canRequest, string? details) => new()
    {
        IsSupported = true, IsConfigured = configured, CanRequestPermission = canRequest, Details = details
    };
}
