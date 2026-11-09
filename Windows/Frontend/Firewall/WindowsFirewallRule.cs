namespace PasswordManagerLocal.Windows.Frontend.Firewall;

internal sealed record WindowsFirewallRule(
    string Name,
    string Grouping,
    string Program,
    int Protocol,
    int Direction,
    int Action,
    bool Enabled,
    int Profiles,
    string LocalPorts,
    string RemotePorts,
    string RemoteAddresses = "*",
    string LocalAddresses = "*",
    string ServiceName = "",
    string InterfaceTypes = "All",
    bool EdgeTraversal = false);
