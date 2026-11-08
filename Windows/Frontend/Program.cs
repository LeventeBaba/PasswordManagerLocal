using Avalonia;
using Avalonia.Controls;
using ReactiveUI.Avalonia;
using PasswordManagerLocal.Common.Frontend;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Windows.Frontend.Activation;
using PasswordManagerLocal.Windows.Frontend.AgentConnection;
using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Coordination;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Frontend.Installation;
using PasswordManagerLocal.Windows.Frontend.Lifecycle;
using PasswordManagerLocal.Windows.Frontend.Notifications;
using PasswordManagerLocal.Windows.Frontend.Settings;
using PasswordManagerLocal.Windows.Frontend.SingleInstance;
using System.Diagnostics;

namespace PasswordManagerLocal.Windows.Frontend;

internal sealed class Program
{
#if PML_WINDOWS_UNIFIED_NATIVE_AOT
    internal const bool UseUnifiedExecutable = true;
#else
    internal const bool UseUnifiedExecutable = false;
#endif

    [STAThread]
    public static void Main(string[] args)
    {
#if PML_WINDOWS_UNIFIED_NATIVE_AOT
        if (args.Contains(WindowsAgentLaunchArguments.AgentRole, StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode = string.Equals(
                args[0], WindowsAgentLaunchArguments.AgentRole, StringComparison.OrdinalIgnoreCase)
                ? global::PasswordManagerLocal.Windows.Agent.Program.Run(args[1..], unifiedExecutable: true)
                : 2;
            return;
        }
#endif

        // Handle the narrow elevated operation before touching user data, instance
        // locks, Agent IPC, or Avalonia. The regular app remains unelevated.
        if (args.Contains(Firewall.WindowsFirewallSetup.Argument, StringComparer.Ordinal))
        {
            Environment.ExitCode = args.Length == 1
                ? Firewall.WindowsFirewallSetup.Run()
                : Firewall.WindowsFirewallSetup.InvalidArgumentsExitCode;
            return;
        }

        if (args.Contains(WindowsInteractiveUninstall.Argument, StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode = args.Length == 1 ? WindowsInteractiveUninstall.Run() : 2;
            return;
        }

        var applicationDataDirectory = new WindowsApplicationDataPathProvider()
            .GetApplicationDataDirectory();
        var names = new WindowsInstanceNameProvider(
            "PasswordManagerLocal",
            applicationDataDirectory,
            new WindowsUserIdentityProvider()).GetNames();
        using var uiProcessLock = new FileProcessInstanceLock(names.UiLockFilePath);
        var singleInstance = new WindowsUiSingleInstanceController(
            uiProcessLock,
            new WindowsUiActivationClient(
                names.UiActivationPipeName,
                IpcPeerRole.Ui));
        var instanceRole = singleInstance
            .EnterAsync()
            .GetAwaiter()
            .GetResult();
        if (instanceRole != WindowsUiInstanceRole.Primary)
            return;

        using var process = Process.GetCurrentProcess();
        var identity = new WindowsUiIpcIdentity(
            Environment.ProcessId,
            process.SessionId,
            Guid.NewGuid());
        var agentConnection = new WindowsAgentControlConnection(
            names.ControlPipeName,
            identity,
            new WindowsAgentLauncher(AppContext.BaseDirectory, unifiedExecutable: UseUnifiedExecutable));
        var backendClient = new WindowsNamedPipeFrontendBackendClient(
            agentConnection,
            new WindowsNamedPipeEndpointRpcConnector(
                names.EndpointPipeName,
                identity));
        using var startupLifetime = new CancellationTokenSource();
        using var activationBridge = new WindowsStartupActivationBridge(new AvaloniaWindowActivationBridge());
        var activationServer = new WindowsUiActivationServer(
            names.UiActivationPipeName,
            activationBridge,
            new AvaloniaUiShutdownBridge(),
            backendClient,
            () => agentConnection.AgentProcessId);
        var exitController = new WindowsUiProcessExitController(
            activationServer,
            uiProcessLock,
            backendClient);
        var startupNotification = new WindowsStartupNotification();
        var exitCode = 0;
        var controlledExit = false;

        try
        {
            // Serve second-launch activation before waiting for the Agent. Early
            // requests are accepted and replayed only after the themed window exists.
            activationServer.StartAsync(startupLifetime.Token).GetAwaiter().GetResult();
            var startupToken = startupLifetime.Token;
            var backendStartup = Task.Run(() => backendClient.ConnectAsync(startupToken), startupToken);
            // Observe failure even if local appearance preparation fails before the UI
            // can join the connection task. The shell owns user-visible retry/recovery.
            _ = ObserveBackendStartupAsync(backendStartup, startupToken);
            using var preferencesNotifier = new WindowsAgentApplicationPreferencesNotifier(
                agentConnection, backendClient);
            var platformServices = new FrontendPlatformServices();
            platformServices.Clipboard.SetPlatformClipboardWriter(new WindowsClipboardWriter());
            FirewallPermissionService.SetPlatformFirewallPermissionManager(
                new WindowsFirewallPermissionManager());
            var frontendContext = new FrontendApplicationContext(
                backendClient,
                new WindowsAgentBackgroundSyncSettingsClient(agentConnection),
                applicationDataDirectory,
                () => exitController.RequestExit(),
                applicationPreferencesChangeNotifier: preferencesNotifier,
                platformServices: platformServices,
                backendConnectionStartup: backendStartup,
                desktopPresentationReady: activationBridge.MarkPresentationReady,
                desktopStopping: () =>
                {
                    activationBridge.Stop();
                    startupLifetime.Cancel();
                },
                desktopStartupFailed: startupNotification.ShowFrontendInitializationFailed);
            exitCode = BuildAvaloniaApp(frontendContext)
                .StartWithClassicDesktopLifetime(
                    args,
                    ShutdownMode.OnExplicitShutdown);
            controlledExit = true;
        }
        finally
        {
            activationBridge.Stop();
            startupLifetime.Cancel();
            if (controlledExit || exitController.IsExitRequested)
            {
                exitController.RequestExit(exitCode);
            }
            else
            {
                new WindowsUiProcessShutdownCoordinator()
                    .ShutdownAsync(
                        activationServer,
                        uiProcessLock,
                        backendClient)
                    .GetAwaiter()
                    .GetResult();
            }
        }
    }

    private static async Task ObserveBackendStartupAsync(Task startup, CancellationToken token)
    {
        try { await startup.ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Trace.TraceError($"Windows backend startup failed: {exception.GetType().Name}");
        }
    }

    public static AppBuilder BuildAvaloniaApp(FrontendApplicationContext frontendContext)
    {
        ArgumentNullException.ThrowIfNull(frontendContext);

        var builder = AppBuilder.Configure(() => new App(frontendContext))
            .UseWin32()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .UseReactiveUI(_ => { });

#if DEBUG
        builder = builder.LogToTrace();
#endif

        return builder;
    }
}
