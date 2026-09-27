using Avalonia;
using Avalonia.Controls;
using ReactiveUI.Avalonia;
using PasswordManagerLocal.Common.Frontend;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Windows.Frontend.Activation;
using PasswordManagerLocal.Windows.Frontend.AgentConnection;
using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Coordination;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Frontend.Lifecycle;
using PasswordManagerLocal.Windows.Frontend.Notifications;
using PasswordManagerLocal.Windows.Frontend.Settings;
using PasswordManagerLocal.Windows.Frontend.SingleInstance;
using System.Diagnostics;

namespace PasswordManagerLocal.Windows.Frontend;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
            new WindowsAgentLauncher(AppContext.BaseDirectory));
        var backendClient = new WindowsNamedPipeFrontendBackendClient(
            agentConnection,
            new WindowsNamedPipeEndpointRpcConnector(
                names.EndpointPipeName,
                identity));
        var activationServer = new WindowsUiActivationServer(
            names.UiActivationPipeName,
            new AvaloniaWindowActivationBridge(),
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
            if (!agentConnection.ConnectAsync().GetAwaiter().GetResult())
            {
                startupNotification.ShowBackendUnavailable();
                controlledExit = true;
                return;
            }

            activationServer.StartAsync().GetAwaiter().GetResult();
            try
            {
                backendClient.ConnectAsync().GetAwaiter().GetResult();
                backendClient.WaitUntilReadyAsync().GetAwaiter().GetResult();
            }
            catch
            {
                if (backendClient.Snapshot.FailureKind != BackendRuntimeFailureKind.DatabaseCompatibility)
                {
                    startupNotification.ShowBackendUnavailable();
                    controlledExit = true;
                    return;
                }

                // The frontend owns the existing compatibility-reset dialog and will issue the agent reset over control IPC.
            }

            var platformServices = new FrontendPlatformServices();
            platformServices.Clipboard.SetPlatformClipboardWriter(new WindowsClipboardWriter());
            FirewallPermissionService.SetPlatformFirewallPermissionManager(
                new WindowsFirewallPermissionManager());
            var frontendContext = new FrontendApplicationContext(
                backendClient,
                new WindowsAgentBackgroundSyncSettingsClient(agentConnection),
                applicationDataDirectory,
                () => exitController.RequestExit(),
                applicationPreferencesChangeNotifier: new WindowsAgentApplicationPreferencesNotifier(agentConnection),
                platformServices: platformServices);
            exitCode = BuildAvaloniaApp(frontendContext)
                .StartWithClassicDesktopLifetime(
                    args,
                    ShutdownMode.OnExplicitShutdown);
            controlledExit = true;
        }
        finally
        {
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
