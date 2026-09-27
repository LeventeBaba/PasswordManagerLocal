using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PasswordManagerLocal.Windows.Agent.Native;

internal sealed class WindowsNativeIcon : IDisposable
{
    private nint _handle;

    private WindowsNativeIcon(nint handle)
    {
        _handle = handle;
    }

    internal nint Handle => Volatile.Read(ref _handle);

    internal static WindowsNativeIcon LoadOwned(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("The tray icon resource was not found.", path);

        var handle = WindowsNativeMethods.LoadImage(
            0,
            path,
            WindowsNativeMethods.ImageIcon,
            0,
            0,
            WindowsNativeMethods.LrLoadFromFile | WindowsNativeMethods.LrDefaultSize);
        if (handle == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new WindowsNativeIcon(handle);
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0 && !WindowsNativeMethods.DestroyIcon(handle))
            Trace.TraceWarning("An owned native tray icon handle could not be destroyed.");
        GC.SuppressFinalize(this);
    }
}
