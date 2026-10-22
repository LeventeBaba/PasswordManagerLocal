using System.Runtime.InteropServices;
using PasswordManagerLocal.Common.Contracts.Constants;

namespace PasswordManagerLocal.Windows.Frontend.Notifications;

public sealed class WindowsStartupNotification
{
    private const uint ErrorIcon = 0x00000010;

    public void ShowBackendUnavailable()
    {
        _ = MessageBox(
            IntPtr.Zero,
            $"{ApplicationBranding.DisplayName} could not connect to its Windows agent backend. The application will close without creating an in-process fallback runtime.",
            ApplicationBranding.DisplayName,
            ErrorIcon);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(
        IntPtr windowHandle,
        string text,
        string caption,
        uint type);
}
