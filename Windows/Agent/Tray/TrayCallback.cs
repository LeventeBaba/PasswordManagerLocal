using PasswordManagerLocal.Windows.Agent.Native;

namespace PasswordManagerLocal.Windows.Agent.Tray;

/// <summary>Decodes the two Shell_NotifyIcon callback protocols without calling Win32.</summary>
internal readonly record struct TrayCallback(TrayCallbackKind Kind, int? X = null, int? Y = null)
{
    internal static TrayCallback Decode(bool version4, uint iconId, nuint wParam, nint lParam)
    {
        var packed = unchecked((uint)lParam.ToInt64());
        var callbackIconId = version4 ? packed >> 16 : unchecked((uint)wParam);
        if (callbackIconId != iconId)
            return default;

        var notification = version4 ? packed & 0xFFFF : packed;
        if (version4)
        {
            if (notification is WindowsNativeMethods.NinSelect or WindowsNativeMethods.NinKeySelect)
                return new(TrayCallbackKind.Open);
            // Only these version-4 notifications carry documented anchor coordinates.
            // Raw WM_RBUTTONUP must not start a second/nested popup.
            if (notification != WindowsNativeMethods.WmContextMenu)
                return default;

            var position = unchecked((uint)wParam);
            var x = unchecked((short)(position & 0xFFFF));
            var y = unchecked((short)(position >> 16));
            return x == -1 && y == -1
                ? new(TrayCallbackKind.ContextMenu)
                : new(TrayCallbackKind.ContextMenu, x, y);
        }

        return notification switch
        {
            WindowsNativeMethods.WmLButtonUp => new(TrayCallbackKind.Open),
            WindowsNativeMethods.WmRButtonUp => new(TrayCallbackKind.ContextMenu),
            _ => default
        };
    }
}
