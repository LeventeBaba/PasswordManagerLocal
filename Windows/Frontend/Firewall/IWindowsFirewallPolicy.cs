namespace PasswordManagerLocal.Windows.Frontend.Firewall;

// Native objects never cross an await or leave the thread that owns this policy.
internal interface IWindowsFirewallPolicy : IDisposable
{
    WindowsFirewallSnapshot Read();
    void Add(WindowsFirewallRule rule);
    void Remove(string name);
}
