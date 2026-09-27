using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PasswordManagerLocal.Windows.Agent.Native;

internal sealed class WindowsNativeMessageWindow : IWindowsNativeMessagePoster, IDisposable
{
    private readonly string _className = $"PasswordManagerLocal.Agent.{Guid.NewGuid():N}";
    private readonly WindowsNativeMethods.WindowProcedure _windowProcedure;
    private readonly nint _instance;
    private GCHandle _selfHandle;
    private ushort _classAtom;
    private nint _window;
    private int _disposed;

    internal WindowsNativeMessageWindow()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The native Windows agent shell requires Windows.");

        OwnerManagedThreadId = Environment.CurrentManagedThreadId;
        _instance = WindowsNativeMethods.GetModuleHandle(null);
        if (_instance == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        _windowProcedure = WindowProcedure;
        var windowClass = new WindowsNativeMethods.WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowsNativeMethods.WindowClassEx>(),
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
            Instance = _instance,
            Cursor = WindowsNativeMethods.LoadCursor(0, WindowsNativeMethods.IdcArrow),
            ClassName = _className
        };
        _classAtom = WindowsNativeMethods.RegisterClassEx(ref windowClass);
        if (_classAtom == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        try
        {
            _window = WindowsNativeMethods.CreateWindowEx(
                0,
                _className,
                "PasswordManagerLocal Agent",
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                _instance,
                GCHandle.ToIntPtr(_selfHandle));
            if (_window == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch
        {
            if (_selfHandle.IsAllocated)
                _selfHandle.Free();
            WindowsNativeMethods.UnregisterClass(_className, _instance);
            _classAtom = 0;
            throw;
        }
    }

    internal event Func<uint, nuint, nint, nint?>? MessageReceived;

    internal int OwnerManagedThreadId { get; }
    internal nint WindowHandle => Volatile.Read(ref _window);

    public bool TryPostMessage(uint message, nuint wParam = 0, nint lParam = 0)
    {
        var window = WindowHandle;
        return Volatile.Read(ref _disposed) == 0 &&
            window != 0 &&
            WindowsNativeMethods.PostMessage(window, message, wParam, lParam);
    }

    internal void RunMessageLoop()
    {
        ThrowIfDisposed();
        while (true)
        {
            var result = WindowsNativeMethods.GetMessage(out var message, 0, 0, 0);
            if (result == 0)
                return;
            if (result < 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            WindowsNativeMethods.TranslateMessage(ref message);
            WindowsNativeMethods.DispatchMessage(ref message);
        }
    }

    internal void Destroy()
    {
        var window = WindowHandle;
        if (window == 0)
            return;
        if (Environment.CurrentManagedThreadId != OwnerManagedThreadId)
            throw new InvalidOperationException("The native message window must be destroyed on its owner thread.");
        if (!WindowsNativeMethods.DestroyWindow(window))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            if (WindowHandle != 0)
            {
                if (Environment.CurrentManagedThreadId != OwnerManagedThreadId)
                {
                    throw new InvalidOperationException(
                        "The native message window must be disposed on its owner thread.");
                }
                if (!WindowsNativeMethods.DestroyWindow(WindowHandle))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (_classAtom != 0)
            {
                if (!WindowsNativeMethods.UnregisterClass(_className, _instance))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                _classAtom = 0;
            }

            if (_selfHandle.IsAllocated)
                _selfHandle.Free();
            GC.SuppressFinalize(this);
        }
        catch
        {
            Volatile.Write(ref _disposed, 0);
            throw;
        }
    }

    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            nint handlePointer;
            if (message == WindowsNativeMethods.WmNcCreate)
            {
                var create = Marshal.PtrToStructure<WindowsNativeMethods.CreateStruct>(lParam);
                handlePointer = create.CreateParameters;
                WindowsNativeMethods.SetWindowUserData(window, handlePointer);
            }
            else
            {
                handlePointer = WindowsNativeMethods.GetWindowUserData(window);
            }

            if (handlePointer != 0 &&
                GCHandle.FromIntPtr(handlePointer).Target is WindowsNativeMessageWindow target)
            {
                return target.ProcessMessage(window, message, wParam, lParam);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError($"A native agent window callback failed: {exception.GetType().Name}");
        }

        return WindowsNativeMethods.DefWindowProc(window, message, wParam, lParam);
    }

    private nint ProcessMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            var handlers = MessageReceived;
            if (handlers is not null)
            {
                foreach (Func<uint, nuint, nint, nint?> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        var result = handler(message, wParam, lParam);
                        if (result.HasValue)
                            return result.Value;
                    }
                    catch (Exception exception)
                    {
                        Trace.TraceError($"A native agent message handler failed: {exception.GetType().Name}");
                    }
                }
            }

            if (message == WindowsNativeMethods.WmClose)
            {
                Destroy();
                return 0;
            }
            if (message == WindowsNativeMethods.WmDestroy)
            {
                WindowsNativeMethods.PostQuitMessage(0);
                return 0;
            }
            if (message == WindowsNativeMethods.WmNcDestroy)
            {
                WindowsNativeMethods.SetWindowUserData(window, 0);
                Volatile.Write(ref _window, 0);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError($"The native agent window procedure failed: {exception.GetType().Name}");
        }

        return WindowsNativeMethods.DefWindowProc(window, message, wParam, lParam);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WindowsNativeMessageWindow));
    }
}
