using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Agent.Native;
using PasswordManagerLocal.Windows.Agent.Tray;

namespace PasswordManagerLocal.Windows.Tests.IPC.Ui;

[TestClass]
public sealed class TrayCallbackTests
{
    [TestMethod]
    public void Version4ContextMenuPreservesSignedScreenCoordinates()
    {
        var callback = TrayCallback.Decode(true, 1, PackPosition(-1920, -120), PackEvent(1, WindowsNativeMethods.WmContextMenu));
        Assert.AreEqual(TrayCallbackKind.ContextMenu, callback.Kind);
        Assert.AreEqual(-1920, callback.X);
        Assert.AreEqual(-120, callback.Y);
    }

    [TestMethod]
    public void Version4KeyboardMenuSentinelRequestsCursorFallback()
    {
        var callback = TrayCallback.Decode(true, 1, PackPosition(-1, -1), PackEvent(1, WindowsNativeMethods.WmContextMenu));
        Assert.AreEqual(TrayCallbackKind.ContextMenu, callback.Kind);
        Assert.IsNull(callback.X);
        Assert.IsNull(callback.Y);
    }

    [TestMethod]
    public void Version4IgnoresRawMouseMessagesAndOtherIconIds()
    {
        Assert.AreEqual(TrayCallbackKind.None, TrayCallback.Decode(true, 1, 0, PackEvent(1, WindowsNativeMethods.WmRButtonUp)).Kind);
        Assert.AreEqual(TrayCallbackKind.None, TrayCallback.Decode(true, 1, 0, PackEvent(1, WindowsNativeMethods.WmLButtonUp)).Kind);
        Assert.AreEqual(TrayCallbackKind.None, TrayCallback.Decode(true, 1, 0, PackEvent(2, WindowsNativeMethods.WmContextMenu)).Kind);
    }

    [TestMethod]
    public void Version4MouseAndKeyboardSelectionOpenOnce()
    {
        Assert.AreEqual(TrayCallbackKind.Open, TrayCallback.Decode(true, 1, 0, PackEvent(1, WindowsNativeMethods.NinSelect)).Kind);
        Assert.AreEqual(TrayCallbackKind.Open, TrayCallback.Decode(true, 1, 0, PackEvent(1, WindowsNativeMethods.NinKeySelect)).Kind);
    }

    [TestMethod]
    public void LegacyCallbacksUseIconIdNotCoordinates()
    {
        var callback = TrayCallback.Decode(false, 1, 1, (nint)WindowsNativeMethods.WmRButtonUp);
        Assert.AreEqual(TrayCallbackKind.ContextMenu, callback.Kind);
        Assert.IsNull(callback.X);
        Assert.AreEqual(TrayCallbackKind.Open, TrayCallback.Decode(false, 1, 1, (nint)WindowsNativeMethods.WmLButtonUp).Kind);
        Assert.AreEqual(TrayCallbackKind.None, TrayCallback.Decode(false, 1, 2, (nint)WindowsNativeMethods.WmRButtonUp).Kind);
    }

    private static nuint PackPosition(short x, short y) => (nuint)((uint)(ushort)x | ((uint)(ushort)y << 16));
    private static nint PackEvent(uint id, uint notification) => (nint)((id << 16) | notification);
}
