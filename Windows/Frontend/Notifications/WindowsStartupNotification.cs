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

    public void ShowExistingInstance()
    {
        _ = MessageBox(IntPtr.Zero,
            "Password Manager Local is already open or still closing. To switch editions, exit it completely from the notification area and try again.",
            ApplicationBranding.DisplayName, ErrorIcon);
    }

    public void ShowFrontendInitializationFailed()
    {
        _ = MessageBox(IntPtr.Zero,
            $"{ApplicationBranding.DisplayName} could not load its appearance and interface. The application will close. Please try opening it again.",
            ApplicationBranding.DisplayName, ErrorIcon);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(
        IntPtr windowHandle,
        string text,
        string caption,
        uint type);
}
