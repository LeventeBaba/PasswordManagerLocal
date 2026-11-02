namespace PasswordManagerLocal.Windows.Frontend.Firewall;

internal sealed record WindowsFirewallProfile(
    int Type,
    bool Enabled,
    bool BlockAllInboundTraffic,
    int DefaultOutboundAction);
