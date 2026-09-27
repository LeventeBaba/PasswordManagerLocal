using System.Security.Principal;

namespace PasswordManagerLocal.Windows.Ipc.Coordination;

public sealed class WindowsUserIdentityProvider : IWindowsUserIdentityProvider
{
    public string GetStableUserIdentifier()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
    }
}
