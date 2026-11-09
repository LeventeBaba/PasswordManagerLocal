namespace PasswordManagerLocal.Windows.Ipc.Abstractions.Coordination;

public interface IWindowsUserIdentityProvider
{
    string GetStableUserIdentifier();
}
