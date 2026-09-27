namespace PasswordManagerLocal.Windows.Ipc.Coordination;

public interface IWindowsUserIdentityProvider
{
    string GetStableUserIdentifier();
}
