using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace PasswordManagerLocal.Windows.Frontend.Activation;

public sealed class AvaloniaUiShutdownBridge : IWindowsUiShutdownBridge
{
    private readonly IWindowsUiDispatcher _dispatcher;

    public AvaloniaUiShutdownBridge()
        : this(new AvaloniaUiDispatcher())
    {
    }

    public AvaloniaUiShutdownBridge(IWindowsUiDispatcher dispatcher) =>
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<bool> ShutdownAsync(CancellationToken cancellationToken = default) =>
        _dispatcher.InvokeAsync(() =>
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return false;

            Dispatcher.UIThread.Post(() => desktop.Shutdown());
            return true;
        }, cancellationToken);
}
