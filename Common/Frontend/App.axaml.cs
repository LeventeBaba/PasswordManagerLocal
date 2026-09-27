using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Frontend.Views;

namespace PasswordManagerLocal.Common.Frontend;

public partial class App : Application
{
    private readonly FrontendApplicationContext? _desktopContext;

    // Android's process Application and the XAML designer use this constructor.
    // Activity-bound state is composed only after MainViewFactory creates its view.
    public App() { }

    public App(FrontendApplicationContext context)
    {
        _desktopContext = context ?? throw new ArgumentNullException(nameof(context));
        if (OperatingSystem.IsWindows())
            WindowsFirewallConfigurationStore.Initialize(context.ApplicationDataDirectory);
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            // This closure holds no Activity, backend client, VM, registry or old view.
            activity.MainViewFactory = static () => new MainView();
        }
        else if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var context = _desktopContext ?? throw new InvalidOperationException(
                "The desktop frontend context was not supplied by its host.");
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var mainWindow = new MainWindow();
            if (OperatingSystem.IsWindows())
                mainWindow.WindowState = WindowState.Maximized;
            var view = (MainView)mainWindow.Content!;
            var session = new FrontendUiSession(view, context, ownsBackendClient: false);
            mainWindow.Closed += (_, _) =>
                _ = CompleteDesktopCloseAsync(mainWindow, session, context, desktop);
            desktop.MainWindow = mainWindow;
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await session.InitializeAsync();
                    if (session.IsDisposed)
                        return;
                    mainWindow.DataContext = session.ViewModel;
                    await TryShowFirewallPermissionPromptAsync(mainWindow, session, context);
                }
                catch (OperationCanceledException) when (session.IsDisposed) { }
                catch (Exception exception)
                {
                    System.Diagnostics.Trace.TraceError(
                        $"Frontend initialization failed: {exception.GetType().Name}");
                }
            }, DispatcherPriority.Background);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task CompleteDesktopCloseAsync(
        MainWindow mainWindow,
        FrontendUiSession session,
        FrontendApplicationContext context,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        try { await session.DisposeAsync(); }
        finally
        {
            mainWindow.DataContext = null;
            context.DesktopExitRequested?.Invoke();
            TryShutdownDesktop(desktop);
        }
    }

    private static void TryShutdownDesktop(
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            desktop.TryShutdown();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task TryShowFirewallPermissionPromptAsync(
        MainWindow window, FrontendUiSession session, FrontendApplicationContext context)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            var token = session.PlatformServices.LifetimeToken;
            await context.BackendClient.WaitUntilReadyAsync(token);
            var endpoints = new DeferredEndpoints(context.BackendClient, token);
            var localDevice = await endpoints.GetLocalDeviceInfoAsync(token);
            if (!session.IsDisposed && localDevice.IsSyncOn && session.ViewModel is { } model)
                await FirewallPermissionStartupPrompt.TryShowAsync(window, model.CurrentLanguage);
        }
        catch { }
    }
}
