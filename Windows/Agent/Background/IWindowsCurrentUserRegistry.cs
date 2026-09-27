namespace PasswordManagerLocal.Windows.Agent.Background;

public interface IWindowsCurrentUserRegistry
{
    WindowsCurrentUserRegistryValue ReadValue(string keyPath, string valueName);

    void WriteString(string keyPath, string valueName, string value);

    void DeleteValue(string keyPath, string valueName);
}
