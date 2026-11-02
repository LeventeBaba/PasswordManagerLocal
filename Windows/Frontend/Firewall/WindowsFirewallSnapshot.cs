namespace PasswordManagerLocal.Windows.Frontend.Firewall;

internal sealed record WindowsFirewallSnapshot(
    int CurrentProfiles,
    int LocalPolicyModifyState,
    IReadOnlyList<WindowsFirewallProfile> Profiles,
    IReadOnlyList<WindowsFirewallRule> Rules);
