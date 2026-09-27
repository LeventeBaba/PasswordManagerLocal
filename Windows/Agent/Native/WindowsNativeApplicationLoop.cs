using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Coordination;
using System.Diagnostics;

namespace PasswordManagerLocal.Windows.Agent.Native;

internal sealed class WindowsNativeApplicationLoop : IDisposable
{
    internal const uint DispatchMessage = WindowsNativeMethods.WmApp + 1;
    internal const uint CloseMessage = WindowsNativeMethods.WmApp + 2;
    internal const uint TrayCallbackMessage = WindowsNativeMethods.WmApp + 3;

    private readonly WindowsNativeMessageWindow _messageWindow;
    private readonly WindowsNativeShellDispatcher _dispatcher;
    private IWindowsAgentHost? _host;
    private WindowsAgentStateStore? _stateStore;
    private string _startupFailureTitle = string.Empty;
    private string _startupFailureMessage = string.Empty;
    private int _started;
    private int _startupCompleted;
    private int _failureShutdownStarted;
    private int _shellShutdownStarted;
    private int _closeRequested;
    private int _disposed;

    internal WindowsNativeApplicationLoop()
    {
        _messageWindow = new WindowsNativeMessageWindow();
        _dispatcher = new WindowsNativeShellDispatcher(
            _messageWindow,
            DispatchMessage,
            _messageWindow.OwnerManagedThreadId);
        _messageWindow.MessageReceived += HandleMessage;
    }

    internal WindowsNativeMessageWindow MessageWindow => _messageWindow;
    internal WindowsNativeShellDispatcher Dispatcher => _dispatcher;
    internal bool ShellFailed { get; private set; }

    internal void Run(
        IWindowsAgentHost host,
        WindowsAgentStateStore stateStore,
        string startupFailureTitle,
        string startupFailureMessage)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(startupFailureTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(startupFailureMessage);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The native Windows agent loop has already run.");

        _host = host;
        _stateStore = stateStore;
        _startupFailureTitle = startupFailureTitle;
        _startupFailureMessage = startupFailureMessage;
        _stateStore.StateChanged += HandleStateChanged;
        if (!_dispatcher.TryPost(() => _ = StartHostAsync()))
            throw new InvalidOperationException("The native shell could not queue agent startup.");

        try
        {
            _messageWindow.RunMessageLoop();
        }
        finally
        {
            _stateStore.StateChanged -= HandleStateChanged;
            _dispatcher.StopAccepting();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_stateStore is not null)
            _stateStore.StateChanged -= HandleStateChanged;
        _messageWindow.MessageReceived -= HandleMessage;
        _dispatcher.Dispose();
        _messageWindow.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task StartHostAsync()
    {
        try
        {
            await _host!.StartAsync();
            Interlocked.Exchange(ref _startupCompleted, 1);
            if (_stateStore!.State == AgentState.Failed)
                BeginFailureShutdown();
            else if (_stateStore.State == AgentState.Stopped)
                RequestClose();
        }
        catch (ProcessInstanceAlreadyOwnedException)
        {
            ShellFailed = true;
            RequestClose();
        }
        catch
        {
            ShellFailed = true;
            if (!_dispatcher.TryPost(ShowStartupFailureAndClose))
            {
                WindowsNativeMessageBox.ShowError(_startupFailureTitle, _startupFailureMessage);
                RequestClose();
            }
        }
    }

    private void ShowStartupFailureAndClose()
    {
        WindowsNativeMessageBox.ShowError(
            _startupFailureTitle,
            _startupFailureMessage,
            _messageWindow.WindowHandle);
        RequestClose();
    }

    private void HandleStateChanged(object? sender, WindowsAgentStateChangedEventArgs args)
    {
        if (args.Current == AgentState.Stopped)
        {
            if (Volatile.Read(ref _startupCompleted) != 0)
                RequestClose();
            return;
        }

        if (args.Current == AgentState.Failed && Volatile.Read(ref _startupCompleted) != 0)
        {
            ShellFailed = true;
            BeginFailureShutdown();
        }
    }

    private void BeginFailureShutdown()
    {
        if (Interlocked.Exchange(ref _failureShutdownStarted, 1) != 0)
            return;
        _ = StopFailedHostAndCloseAsync();
    }

    private async Task StopFailedHostAndCloseAsync()
    {
        try { await _host!.ShutdownAsync(); }
        catch (Exception exception)
        {
            Trace.TraceError($"The failed native agent host could not finish shutdown: {exception.GetType().Name}");
        }
        finally
        {
            RequestClose();
        }
    }

    private void BeginShellShutdown()
    {
        if (Interlocked.Exchange(ref _shellShutdownStarted, 1) != 0)
            return;
        _ = StopHostAndCloseAsync();
    }

    private async Task StopHostAndCloseAsync()
    {
        try
        {
            if (_host is not null)
                await _host.ShutdownAsync();
        }
        catch (Exception exception)
        {
            ShellFailed = true;
            Trace.TraceError($"The native agent host could not finish shell-requested shutdown: {exception.GetType().Name}");
        }
        finally
        {
            RequestClose();
        }
    }

    private void RequestClose()
    {
        if (Interlocked.Exchange(ref _closeRequested, 1) != 0)
            return;
        if (!_messageWindow.TryPostMessage(CloseMessage))
            Trace.TraceError("The native agent shell could not post its close message.");
    }

    private nint? HandleMessage(uint message, nuint wParam, nint lParam)
    {
        if (message == DispatchMessage)
        {
            _dispatcher.DrainPendingActions();
            return 0;
        }
        if (message == CloseMessage)
        {
            _dispatcher.StopAccepting();
            _messageWindow.Destroy();
            return 0;
        }
        if (message == WindowsNativeMethods.WmClose)
        {
            BeginShellShutdown();
            return 0;
        }

        return null;
    }
}
