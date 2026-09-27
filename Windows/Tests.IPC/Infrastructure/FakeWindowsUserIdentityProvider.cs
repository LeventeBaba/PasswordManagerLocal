using PasswordManagerLocal.Windows.Ipc.Coordination;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class FakeWindowsUserIdentityProvider : IWindowsUserIdentityProvider
{
    private readonly string _identifier;

    public FakeWindowsUserIdentityProvider(string identifier)
    {
        _identifier = identifier;
    }

    public string GetStableUserIdentifier() => _identifier;
}
