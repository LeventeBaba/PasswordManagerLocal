using PasswordManagerLocal.Windows.Agent.Native;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PasswordManagerLocal.Windows.Agent.Tray;

internal sealed class WindowsNativeTrayIconAdapter : ITrayIconAdapter
{
    private const uint IconId = 1;
    private const uint OpenCommandId = 1001;
    private const uint ExitCommandId = 1002;

    private readonly string _iconPath;
    private WindowsAgentTrayText _text;
    private readonly WindowsNativeMessageWindow _messageWindow;
    private readonly WindowsNativeShellDispatcher _dispatcher;
    private WindowsNativeIcon? _icon;
    private uint _taskbarCreatedMessage;
    private bool _initialized;
    private bool _visibleRequested;
    private bool _isAdded;
    private bool _version4Enabled;
    private bool _contextMenuOpen;
    private int _disposed;

    internal WindowsNativeTrayIconAdapter(
        string iconPath,
        WindowsAgentTrayText text,
        WindowsNativeMessageWindow messageWindow,
        WindowsNativeShellDispatcher dispatcher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iconPath);
        _iconPath = Path.GetFullPath(iconPath);
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _messageWindow = messageWindow ?? throw new ArgumentNullException(nameof(messageWindow));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _messageWindow.MessageReceived += HandleWindowMessage;
    }

    public event EventHandler<TrayIconMouseEventArgs>? MouseClicked;
    public event EventHandler? ContextMenuOpening;
    public event EventHandler? OpenCommandSelected;
    public event EventHandler? ExitCommandSelected;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (_initialized)
                throw new InvalidOperationException("The native tray icon has already been initialized.");

            try
            {
                _icon = WindowsNativeIcon.LoadOwned(_iconPath);
                _taskbarCreatedMessage = WindowsNativeMethods.RegisterWindowMessage("TaskbarCreated");
                if (_taskbarCreatedMessage == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                _initialized = true;
            }
            catch
            {
                _icon?.Dispose();
                _icon = null;
                throw;
            }
        }, cancellationToken);

    public Task SetVisibleAsync(
        bool isVisible,
        CancellationToken cancellationToken = default) =>
        _dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (!_initialized)
                throw new InvalidOperationException("The native tray icon has not been initialized.");

            _visibleRequested = isVisible;
            if (isVisible)
                AddIconIfNeeded();
            else
                RemoveIconIfNeeded(throwOnFailure: true);
        }, cancellationToken);


    public Task UpdateTextAsync(
        WindowsAgentTrayText text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            _text = text;
            if (!_isAdded)
                return;

            var data = CreateNotifyIconData(WindowsNativeMethods.NifTip);
            if (!WindowsNativeMethods.ShellNotifyIcon(WindowsNativeMethods.NimModify, ref data))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }, cancellationToken);
    }

    public Task ShowErrorAsync(
        string safeMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        return _dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (!_isAdded)
                return;

            var data = CreateNotifyIconData(WindowsNativeMethods.NifInfo);
            data.Info = Truncate(safeMessage, 255);
            data.InfoTitle = Truncate(_text.ErrorTitle, 63);
            data.InfoFlags = WindowsNativeMethods.NiifError;
            data.TimeoutOrVersion = 4000;
            if (!WindowsNativeMethods.ShellNotifyIcon(WindowsNativeMethods.NimModify, ref data))
                Trace.TraceError("The native tray error notification could not be displayed.");
        }, cancellationToken);
    }

    public Task HideAndDisposeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return Task.CompletedTask;

        return _dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _visibleRequested = false;
            RemoveIconIfNeeded(throwOnFailure: false);
            _icon?.Dispose();
            _icon = null;
            _initialized = false;
            _messageWindow.MessageReceived -= HandleWindowMessage;
        }, cancellationToken);
    }

    private nint? HandleWindowMessage(uint message, nuint wParam, nint lParam)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_initialized)
            return null;

        if (message == _taskbarCreatedMessage)
        {
            _isAdded = false;
            _version4Enabled = false;
            if (_visibleRequested)
            {
                try { AddIconIfNeeded(); }
                catch (Exception exception)
                {
                    Trace.TraceError($"The tray icon could not be restored after Explorer restart: {exception.GetType().Name}");
                }
            }
            return 0;
        }

        if (message != WindowsNativeApplicationLoop.TrayCallbackMessage)
            return null;

        var callback = TrayCallback.Decode(_version4Enabled, IconId, wParam, lParam);
        if (callback.Kind == TrayCallbackKind.Open)
        {
            PublishMouseClick(TrayIconMouseButton.Left);
            return 0;
        }
        if (callback.Kind == TrayCallbackKind.ContextMenu)
        {
            ShowContextMenu(callback);
            return 0;
        }

        return 0;
    }

    private void AddIconIfNeeded()
    {
        if (_isAdded)
            return;
        if (_icon is null || _icon.Handle == 0)
            throw new InvalidOperationException("The native tray icon resource is unavailable.");

        var data = CreateNotifyIconData(
            WindowsNativeMethods.NifMessage |
            WindowsNativeMethods.NifIcon |
            WindowsNativeMethods.NifTip |
            WindowsNativeMethods.NifShowTip);
        if (!WindowsNativeMethods.ShellNotifyIcon(WindowsNativeMethods.NimAdd, ref data))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        _isAdded = true;
        var version = CreateNotifyIconData(0);
        version.TimeoutOrVersion = WindowsNativeMethods.NotifyIconVersion4;
        _version4Enabled = WindowsNativeMethods.ShellNotifyIcon(
            WindowsNativeMethods.NimSetVersion,
            ref version);
        if (!_version4Enabled)
            Trace.TraceWarning("The tray icon could not enable NOTIFYICON_VERSION_4 semantics.");
    }

    private void RemoveIconIfNeeded(bool throwOnFailure)
    {
        if (!_isAdded)
            return;

        var data = CreateNotifyIconData(0);
        var removed = WindowsNativeMethods.ShellNotifyIcon(WindowsNativeMethods.NimDelete, ref data);
        _isAdded = false;
        _version4Enabled = false;
        if (!removed && throwOnFailure)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!removed)
            Trace.TraceWarning("The native tray icon could not be removed cleanly.");
    }

    private void ShowContextMenu(TrayCallback callback)
    {
        if (!_isAdded || _contextMenuOpen)
            return;

        _contextMenuOpen = true;
        nint menu = 0;
        uint command = 0;
        try
        {
            // The preference coordinator only queues work here; no disk IO on this thread.
            Publish(ContextMenuOpening);
            var text = _text;
            menu = WindowsNativeMethods.CreatePopupMenu();
            if (menu == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            AppendMenu(menu, WindowsNativeMethods.MfString, OpenCommandId, text.OpenLabel);
            AppendMenu(menu, WindowsNativeMethods.MfSeparator, 0, null);
            AppendMenu(menu, WindowsNativeMethods.MfString, ExitCommandId, text.ExitLabel);
            var point = ResolveMenuPosition(callback);

            if (!WindowsNativeMethods.SetForegroundWindow(_messageWindow.WindowHandle))
                Trace.TraceWarning("The Agent tray menu owner could not become the foreground window.");
            Marshal.SetLastPInvokeError(0);
            command = WindowsNativeMethods.TrackPopupMenuEx(
                menu,
                WindowsNativeMethods.TpmRightButton |
                WindowsNativeMethods.TpmReturnCmd |
                WindowsNativeMethods.TpmNonotify,
                point.X,
                point.Y,
                _messageWindow.WindowHandle,
                0);
            var error = Marshal.GetLastWin32Error();
            if (command == 0 && error != 0)
                Trace.TraceError($"The Agent tray popup failed (Win32 error {error}).");
            // Zero without an error is normal cancellation, not an Open/Exit command.
        }
        catch (Win32Exception exception)
        {
            Trace.TraceError($"The Agent tray menu could not be displayed (Win32 error {exception.NativeErrorCode}).");
        }
        finally
        {
            if (!WindowsNativeMethods.PostMessage(_messageWindow.WindowHandle, WindowsNativeMethods.WmNull, 0, 0))
                Trace.TraceWarning("The Agent tray owner could not receive WM_NULL after popup tracking.");
            if (_isAdded && Volatile.Read(ref _disposed) == 0)
            {
                var data = CreateNotifyIconData(0);
                if (!WindowsNativeMethods.ShellNotifyIcon(WindowsNativeMethods.NimSetFocus, ref data))
                    Trace.TraceWarning("Notification-area focus could not be restored after the Agent popup.");
            }
            if (menu != 0 && !WindowsNativeMethods.DestroyMenu(menu))
                Trace.TraceWarning("A native tray context-menu handle could not be destroyed.");
            _contextMenuOpen = false;
        }

        // Tracking pumps a nested message loop. Recheck lifetime, then dispatch exactly
        // once, on the shell thread, after releasing the menu and restoring shell focus.
        if (Volatile.Read(ref _disposed) != 0 || !_isAdded)
            return;
        if (command == OpenCommandId)
            Publish(OpenCommandSelected);
        else if (command == ExitCommandId)
            Publish(ExitCommandSelected);
    }

    private static WindowsNativeMethods.Point ResolveMenuPosition(TrayCallback callback)
    {
        if (callback.X is { } x && callback.Y is { } y)
            return new WindowsNativeMethods.Point { X = x, Y = y };

        if (!WindowsNativeMethods.GetCursorPosition(out var cursor))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return cursor;
    }

    private WindowsNativeMethods.NotifyIconData CreateNotifyIconData(uint flags) =>
        new()
        {
            Size = (uint)Marshal.SizeOf<WindowsNativeMethods.NotifyIconData>(),
            Window = _messageWindow.WindowHandle,
            Id = IconId,
            Flags = flags,
            CallbackMessage = WindowsNativeApplicationLoop.TrayCallbackMessage,
            Icon = _icon?.Handle ?? 0,
            Tip = Truncate(_text.ToolTip, 127),
            Info = string.Empty,
            InfoTitle = string.Empty
        };

    private static void AppendMenu(nint menu, uint flags, uint id, string? text)
    {
        if (!WindowsNativeMethods.AppendMenu(menu, flags, (nuint)id, text))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private void PublishMouseClick(TrayIconMouseButton button)
    {
        var handlers = MouseClicked;
        if (handlers is null)
            return;
        var args = new TrayIconMouseEventArgs(button, 1);
        foreach (EventHandler<TrayIconMouseEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception exception)
            {
                Trace.TraceError($"A native tray mouse callback failed: {exception.GetType().Name}");
            }
        }
    }

    private void Publish(EventHandler? handlers)
    {
        if (handlers is null)
            return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception exception)
            {
                Trace.TraceError($"A native tray command callback failed: {exception.GetType().Name}");
            }
        }
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WindowsNativeTrayIconAdapter));
    }
}
