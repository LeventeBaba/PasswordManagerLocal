namespace PasswordManagerLocal.Windows.Agent.Native;

internal static class WindowsNativeMessageBox
{
    internal static void ShowError(string title, string safeMessage, nint owner = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        WindowsNativeMethods.MessageBox(
            owner,
            safeMessage,
            title,
            WindowsNativeMethods.MbOk |
            WindowsNativeMethods.MbIconError |
            WindowsNativeMethods.MbSetForeground);
    }
}
