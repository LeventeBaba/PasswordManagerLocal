using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Frontend.Theming;
using PasswordManagerLocal.Common.Frontend.Views;

namespace PasswordManagerLocal.Common.Frontend;

public partial class App : Application
{
    private readonly FrontendApplicationContext? _desktopContext;

    // Android's process Application and the XAML designer use this constructor.
    // Activity-bound state is composed only after MainViewFactory creates its view.
    public App()
    {
        ThemeResourceHost = new AvaloniaAppThemeResourceHost(this);
    }

    public App(FrontendApplicationContext context)
        : this()
    {
        _desktopContext = context ?? throw new ArgumentNullException(nameof(context));
        if (OperatingSystem.IsWindows())
            WindowsFirewallConfigurationStore.Initialize(context.ApplicationDataDirectory);
    }

    internal AvaloniaAppThemeResourceHost ThemeResourceHost { get; }

    private bool _featureControlThemesLoaded;

    internal void EnsureFeatureControlThemes()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_featureControlThemesLoaded) return;
        Resources.MergedDictionaries.Add(new FeatureControlThemes());
        _featureControlThemesLoaded = true;
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
            var firewallPromptCompleted = false;
            var firewallPromptRunning = false;
            session.BackendInitializationCompleted += async (_, _) =>
            {
                if (session.IsDisposed || !mainWindow.IsVisible || firewallPromptCompleted || firewallPromptRunning ||
                    context.BackendClient.Snapshot.IsReady != true || session.ViewModel?.IsDatabaseRecoveryDialogOpen == true)
                    return;
                firewallPromptRunning = true;
                try
                {
                    await TryShowFirewallPermissionPromptAsync(mainWindow, session, context);
                    firewallPromptCompleted = true;
                }
                finally { firewallPromptRunning = false; }
            };
            mainWindow.Closed += (_, _) =>
                _ = CompleteDesktopCloseAsync(mainWindow, session, context, desktop);
            desktop.Exit += (_, _) =>
            {
                context.DesktopStopping?.Invoke();
                _ = DisposeDesktopSessionAsync(session);
            };
            // The desktop lifetime automatically shows MainWindow when its event loop
            // starts. Do not publish it yet: the selected theme and the first view
            // model are loaded asynchronously, and showing it early exposes an
            // unstyled (usually white) window before the first usable frame.
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await session.PreparePresentationAsync();
                    if (session.IsDisposed)
                    {
                        AbortUnshownDesktopStartup(desktop);
                        return;
                    }
                    if (session.ViewModel is null)
                        throw new InvalidOperationException("The desktop UI did not initialize.");

                    mainWindow.DataContext = session.ViewModel;
                    desktop.MainWindow = mainWindow;
                    mainWindow.Show();
                    context.DesktopPresentationReady?.Invoke();
                    // Backend and automatic sign-in continue after presentation. Failure
                    // is rendered in the shell; it does not close a usable themed UI.
                    _ = CompleteDesktopInitializationAsync(session);
                }
                catch (OperationCanceledException) when (session.IsDisposed)
                {
                    AbortUnshownDesktopStartup(desktop);
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Trace.TraceError(
                        $"Frontend initialization failed: {exception.GetType().Name}");
                    // Do not leave an invisible UI process behind if startup fails.
                    context.DesktopStopping?.Invoke();
                    await DisposeDesktopSessionAsync(session);
                    context.DesktopStartupFailed?.Invoke();
                    AbortUnshownDesktopStartup(desktop);
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
        context.DesktopStopping?.Invoke();
        // DisposeAsync revokes UI admission synchronously. Let the existing process
        // exit controller bound transport cleanup instead of waiting invisibly for
        // an in-flight backend operation after the user has closed the window.
        var cleanup = DisposeDesktopSessionAsync(session);
        mainWindow.DataContext = null;
        context.DesktopExitRequested?.Invoke();
        TryShutdownDesktop(desktop);
        await cleanup;
    }

    private static async Task DisposeDesktopSessionAsync(FrontendUiSession session)
    {
        try { await session.DisposeAsync(); }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"Frontend cleanup failed: {exception.GetType().Name}");
        }
    }

    private static async Task CompleteDesktopInitializationAsync(FrontendUiSession session)
    {
        try
        {
            // Give the dispatcher a rendering/input opportunity before beginning
            // restoration, including when the initial connection already completed.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (session.IsDisposed)
                return;
            await session.InitializeAsync();
        }
        catch (OperationCanceledException) when (session.IsDisposed) { }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"Frontend backend initialization failed: {exception.GetType().Name}");
        }
    }

    private static void AbortUnshownDesktopStartup(
        IClassicDesktopStyleApplicationLifetime desktop) => TryShutdownDesktop(desktop);

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
            if (!session.IsDisposed &&
                localDevice.IsSyncOn &&
                session.UiPreferences is { } preferences)
            {
                await FirewallPermissionStartupPrompt.TryShowAsync(window, preferences, token);
            }
        }
        catch { }
    }
}
