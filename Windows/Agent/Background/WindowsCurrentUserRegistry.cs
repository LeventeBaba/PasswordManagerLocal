using Microsoft.Win32;

namespace PasswordManagerLocal.Windows.Agent.Background;

public sealed class WindowsCurrentUserRegistry : IWindowsCurrentUserRegistry
{
    public WindowsCurrentUserRegistryValue ReadValue(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        if (key is null || !key.GetValueNames().Contains(
            valueName,
            StringComparer.OrdinalIgnoreCase))
        {
            return new WindowsCurrentUserRegistryValue(false, null);
        }

        var value = key.GetValue(
            valueName,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new WindowsCurrentUserRegistryValue(true, value as string);
    }

    public void WriteString(string keyPath, string valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new UnauthorizedAccessException("The current-user registry key is unavailable.");
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    public void DeleteValue(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
