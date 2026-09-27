using PasswordManagerLocal.Common.Frontend.Services;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PasswordManagerLocal.Windows.Frontend;

internal sealed class WindowsClipboardWriter : IClipboardWriter
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    public Task<bool> TrySetTextAsync(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (TrySetText(text))
            {
                return Task.FromResult(true);
            }

            Thread.Sleep(40);
        }

        return Task.FromResult(false);
    }



    private static bool TrySetText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        var handle = IntPtr.Zero;

        try
        {
            if (!EmptyClipboard())
            {
                return false;
            }

            var byteCount = checked((text.Length + 1) * 2);
            handle = GlobalAlloc(GmemMoveable, (UIntPtr)byteCount);

            if (handle == IntPtr.Zero)
            {
                return false;
            }

            var lockedHandle = GlobalLock(handle);

            if (lockedHandle == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, lockedHandle, text.Length);
                Marshal.WriteInt16(lockedHandle, text.Length * 2, 0);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            if (SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero)
            {
                return false;
            }

            handle = IntPtr.Zero;
            return true;
        }
        finally
        {
            CloseClipboard();

            if (handle != IntPtr.Zero)
            {
                GlobalFree(handle);
            }
        }
    }



    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memoryHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr byteCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memoryHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memoryHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memoryHandle);
}
