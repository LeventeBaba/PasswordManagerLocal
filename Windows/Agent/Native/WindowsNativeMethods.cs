using System.Runtime.InteropServices;

namespace PasswordManagerLocal.Windows.Agent.Native;

internal static class WindowsNativeMethods
{
    internal const uint WmNull = 0x0000;
    internal const uint WmDestroy = 0x0002;
    internal const uint WmClose = 0x0010;
    internal const uint WmCommand = 0x0111;
    internal const uint WmContextMenu = 0x007B;
    internal const uint WmNcCreate = 0x0081;
    internal const uint WmNcDestroy = 0x0082;
    internal const uint WmLButtonUp = 0x0202;
    internal const uint WmRButtonUp = 0x0205;
    internal const uint WmApp = 0x8000;
    internal const uint WmUser = 0x0400;

    internal const int GwlpUserData = -21;
    internal const uint ImageIcon = 1;
    internal const uint LrDefaultSize = 0x0040;
    internal const uint LrLoadFromFile = 0x0010;
    internal const uint MfString = 0x0000;
    internal const uint MfSeparator = 0x0800;
    internal const uint TpmRightButton = 0x0002;
    internal const uint TpmNonotify = 0x0080;
    internal const uint TpmReturnCmd = 0x0100;

    internal const uint NimAdd = 0x00000000;
    internal const uint NimModify = 0x00000001;
    internal const uint NimDelete = 0x00000002;
    internal const uint NimSetFocus = 0x00000003;
    internal const uint NimSetVersion = 0x00000004;
    internal const uint NifMessage = 0x00000001;
    internal const uint NifIcon = 0x00000002;
    internal const uint NifTip = 0x00000004;
    internal const uint NifInfo = 0x00000010;
    internal const uint NifShowTip = 0x00000080;
    internal const uint NiifError = 0x00000003;
    internal const uint NotifyIconVersion4 = 4;
    internal const uint NinSelect = WmUser;
    internal const uint NinKeySelect = WmUser + 1;

    internal const uint MbOk = 0x00000000;
    internal const uint MbIconError = 0x00000010;
    internal const uint MbSetForeground = 0x00010000;

    internal static readonly nint IdcArrow = new(32512);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClassEx
    {
        internal uint Size;
        internal uint Style;
        internal nint WindowProcedure;
        internal int ClassExtraBytes;
        internal int WindowExtraBytes;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint BackgroundBrush;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] internal string ClassName;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Point Point;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CreateStruct
    {
        internal nint CreateParameters;
        internal nint Instance;
        internal nint Menu;
        internal nint Parent;
        internal int Height;
        internal int Width;
        internal int Y;
        internal int X;
        internal int Style;
        internal nint Name;
        internal nint Class;
        internal uint ExtendedStyle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        internal uint Size;
        internal nint Window;
        internal uint Id;
        internal uint Flags;
        internal uint CallbackMessage;
        internal nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State;
        internal uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid GuidItem;
        internal nint BalloonIcon;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", SetLastError = true)]
    internal static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    internal static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);

    [DllImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static extern nint DispatchMessage(ref Message message);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "PostQuitMessage")]
    internal static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint window, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)]
    internal static extern nint LoadCursor(nint instance, nint cursorName);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint LoadImage(
        nint instance,
        string name,
        uint type,
        int desiredWidth,
        int desiredHeight,
        uint loadFlags);

    [DllImport("user32.dll", EntryPoint = "DestroyIcon", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(nint icon);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterWindowMessage(string messageName);

    [DllImport("user32.dll", EntryPoint = "CreatePopupMenu", SetLastError = true)]
    internal static extern nint CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AppendMenu(nint menu, uint flags, nuint item, string? text);

    [DllImport("user32.dll", EntryPoint = "TrackPopupMenuEx", SetLastError = true)]
    internal static extern uint TrackPopupMenuEx(
        nint menu,
        uint flags,
        int x,
        int y,
        nint window,
        nint parameters);

    [DllImport("user32.dll", EntryPoint = "DestroyMenu", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", EntryPoint = "GetCursorPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPosition(out Point point);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    internal static extern int MessageBox(nint owner, string text, string caption, uint type);

    internal static nint SetWindowUserData(nint window, nint value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(window, GwlpUserData, value)
            : new nint(SetWindowLong32(window, GwlpUserData, value.ToInt32()));

    internal static nint GetWindowUserData(nint window) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(window, GwlpUserData)
            : new nint(GetWindowLong32(window, GwlpUserData));
}
