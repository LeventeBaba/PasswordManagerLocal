using PasswordManagerLocal.Windows.Agent.Backend;
using PasswordManagerLocal.Windows.Agent.Background;
using PasswordManagerLocal.Windows.Agent.Lifecycle;
using PasswordManagerLocal.Windows.Agent.Localization;
using PasswordManagerLocal.Windows.Agent.Native;
using PasswordManagerLocal.Windows.Agent.Preferences;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Preferences;
using PasswordManagerLocal.Windows.Agent.DatabaseReset;
using PasswordManagerLocal.Windows.Agent.Endpoint;
using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Windows.Agent.Status;
using PasswordManagerLocal.Windows.Agent.Tray;
using PasswordManagerLocal.Windows.Agent.Ui;
using PasswordManagerLocal.Windows.Ipc.Authorization;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Coordination;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Ipc.Transport;
using PasswordManagerLocal.Windows.Ipc.Validation;
using System.Diagnostics;

namespace PasswordManagerLocal.Windows.Agent;

internal sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AgentLocalizer? localizer = null;
        var bootstrapLanguage = ApplicationPreferencesDefaults.Create().Language;
        WindowsAgentApplicationPreferencesReader? applicationPreferencesReader = null;
        IApplicationPreferencesFileStampProvider? preferenceStampProvider = null;
        ApplicationPreferencesFileStamp initialPreferenceStamp = default;
        string applicationDataDirectory;
        try
        {
            applicationDataDirectory = new WindowsApplicationDataPathProvider()
                .GetApplicationDataDirectory();
            var preferencesStore = new FileApplicationPreferencesStore(applicationDataDirectory);
            applicationPreferencesReader = new WindowsAgentApplicationPreferencesReader(preferencesStore);
            preferenceStampProvider = new PhysicalApplicationPreferencesFileStampProvider(
                applicationDataDirectory);
            initialPreferenceStamp = ApplicationPreferencesFileStamp.Unknown;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var stampBeforeRead = preferenceStampProvider.GetStamp();
                bootstrapLanguage = applicationPreferencesReader.ReadLanguageAsync()
                    .GetAwaiter()
                    .GetResult();
                localizer = AgentLocalizer.CreateAsync(bootstrapLanguage)
                    .GetAwaiter()
                    .GetResult();
                var stampAfterRead = preferenceStampProvider.GetStamp();
                if (stampBeforeRead == stampAfterRead)
                {
                    initialPreferenceStamp = stampAfterRead;
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError($"The Agent localization bootstrap failed: {exception.GetType().Name}");
            TryShowStartupFailure(localizer, bootstrapLanguage);
            return (int)WindowsAgentExitCode.ShellFailure;
        }

        WindowsAgentCommandLineOptions commandLine;
        try
        {
            commandLine = new WindowsAgentCommandLineParser().Parse(args);
        }
        catch (Exception exception)
        {
            Trace.TraceError($"The Agent command line could not be parsed: {exception.GetType().Name}");
            TryShowStartupFailure(localizer, bootstrapLanguage);
            return (int)WindowsAgentExitCode.ShellFailure;
        }

        var names = new WindowsInstanceNameProvider(
            "PasswordManagerLocal",
            applicationDataDirectory,
            new WindowsUserIdentityProvider()).GetNames();

        FileProcessInstanceLock processLock;
        try
        {
            processLock = new FileProcessInstanceLock(names.AgentLockFilePath);
        }
        catch (Exception exception)
        {
            Trace.TraceError($"The Agent process lock could not be acquired: {exception.GetType().Name}");
            TryShowStartupFailure(localizer, bootstrapLanguage);
            return (int)WindowsAgentExitCode.OwnershipFailure;
        }

        if (!processLock.IsOwner)
        {
            processLock.Dispose();
            return (int)WindowsAgentExitCode.AlreadyRunning;
        }

        WindowsAgentHost? host = null;
        WindowsNativeApplicationLoop? applicationLoop = null;
        AgentApplicationPreferencesReloadCoordinator? preferencesReloadCoordinator = null;
        var exitCode = WindowsAgentExitCode.ShellFailure;

        void ShowShutdownFailure()
        {
            try
            {
                WindowsNativeMessageBox.ShowError(
                    localizer!.GetString(AgentLocalizationKeys.ShutdownFailureTitle),
                    localizer.GetString(AgentLocalizationKeys.ShutdownFailureMessage));
            }
            catch (Exception exception)
            {
                Trace.TraceError($"The localized Agent shutdown failure could not be displayed: {exception.GetType().Name}");
            }
        }

        try
        {
            var stateStore = new WindowsAgentStateStore();
            var admissionGate = new WindowsAgentAdmissionGate();
            var uiCoordinator = new SingleUiConnectionCoordinator();
            var shutdownCoordinator = new WindowsAgentShutdownCoordinator();
            var activationClient = new WindowsUiActivationClient(
                names.UiActivationPipeName,
                IpcPeerRole.Agent);
            var uiLauncher = new WindowsUiLauncher(
                AppContext.BaseDirectory,
                localizer!);
            var uiOpenService = new WindowsUiOpenService(
                activationClient,
                uiLauncher,
                localizer!);
            var uiCloseService = new WindowsUiCloseService(
                activationClient,
                localizer!);

            var backendOwner = new WindowsAgentBackendRuntimeOwner();
            var lifecycleTransitions = new WindowsAgentLifecycleTransitionCoordinator();
            var settingsStore = new FileBackgroundSyncSettingsStore(applicationDataDirectory);
            var processPath = Environment.ProcessPath;
            var agentExecutablePath = !string.IsNullOrWhiteSpace(processPath) &&
                string.Equals(
                    Path.GetFileName(processPath),
                    WindowsStartupRegistrationConstants.AgentExecutableFileName,
                    StringComparison.OrdinalIgnoreCase)
                ? processPath
                : Path.Combine(
                    AppContext.BaseDirectory,
                    WindowsStartupRegistrationConstants.AgentExecutableFileName);
            var startupRegistration = new WindowsRunStartupRegistration(
                new WindowsAgentStartupCommand(agentExecutablePath));
            var backgroundSyncCoordinator = new WindowsBackgroundSyncCoordinator(
                settingsStore,
                startupRegistration,
                backendOwner,
                stateStore,
                admissionGate,
                lifecycleTransitions,
                localizer!);
            applicationLoop = new WindowsNativeApplicationLoop();
            var trayText = WindowsAgentTrayText.Create(localizer!);
            var trayController = new WindowsTrayIconController(
                new WindowsNativeTrayIconAdapter(
                    Path.Combine(AppContext.BaseDirectory, "Assets", "app_icon.ico"),
                    trayText,
                    applicationLoop.MessageWindow,
                    applicationLoop.Dispatcher));
            preferencesReloadCoordinator = new AgentApplicationPreferencesReloadCoordinator(
                applicationPreferencesReader!,
                localizer!,
                trayController,
                preferenceStampProvider!,
                initialPreferenceStamp);
            var endpointAdapter = new AgentInteractiveEndpointAdapter(backendOwner);
            var registrationResolver = new RegisteredUiEndpointRegistrationResolver(
                uiCoordinator,
                admissionGate);
            var endpointHost = new WindowsAgentEndpointHost(
                names.EndpointPipeName,
                endpointAdapter,
                registrationResolver,
                admissionGate,
                stateStore,
                backendOwner);
            var resetCoordinator = new WindowsAgentDatabaseResetCoordinator(
                endpointHost,
                backendOwner,
                shutdownCoordinator,
                backgroundSyncCoordinator,
                lifecycleTransitions,
                localizer!);
            var statusProvider = new WindowsAgentStatusProvider(
                stateStore,
                admissionGate,
                uiCoordinator,
                backgroundSyncCoordinator,
                backendOwner,
                endpointHost,
                endpointAdapter,
                resetCoordinator,
                localizer!);

            var serializer = new WindowsIpcSerializer();
            var validator = new WindowsIpcContractValidator();
            var handlers = new IWindowsIpcRequestHandler[]
            {
                new PingWindowsIpcRequestHandler(),
                new GetAgentStatusWindowsIpcRequestHandler(statusProvider, validator),
                new GetBackendRuntimeStatusWindowsIpcRequestHandler(statusProvider, validator),
                new GetInteractiveSessionStatusWindowsIpcRequestHandler(statusProvider, validator),
                new GetSynchronizationStatusWindowsIpcRequestHandler(statusProvider, validator),
                new GetBackgroundSyncStateWindowsIpcRequestHandler(
                    backgroundSyncCoordinator,
                    validator),
                new WindowsAgentAdmissionRequestHandler(
                    admissionGate,
                    new RegisterUiConnectionWindowsIpcRequestHandler(uiCoordinator),
                    localizer!),
                new WindowsAgentAdmissionRequestHandler(
                    admissionGate,
                    new UnregisterUiConnectionWindowsIpcRequestHandler(uiCoordinator),
                    localizer!),
                new WindowsAgentAdmissionRequestHandler(
                    admissionGate,
                    new WindowsAgentRequestUiOpenHandler(uiOpenService),
                    localizer!),
                new WindowsAgentAdmissionRequestHandler(
                    admissionGate,
                    new WindowsAgentRequestUiActivationHandler(uiOpenService),
                    localizer!),
                new WindowsAgentAdmissionRequestHandler(
                    admissionGate,
                    new RequestAgentExitWindowsIpcRequestHandler(
                        new WindowsAgentExitRequestSink(shutdownCoordinator)),
                    localizer!),
                new WindowsAgentAdmissionRequestHandler(
                    admissionGate,
                    new ResetDatabaseWindowsIpcRequestHandler(resetCoordinator),
                    localizer!),
                new WindowsAgentAdmissionRequestHandler(
                    admissionGate,
                    new SetBackgroundSyncEnabledWindowsIpcRequestHandler(
                        backgroundSyncCoordinator,
                        validator,
                        trayController),
                    localizer!),
                new WindowsAgentAdmissionRequestHandler(
                    admissionGate,
                    new ReloadApplicationPreferencesWindowsIpcRequestHandler(
                        preferencesReloadCoordinator),
                    localizer!)
            };
            var dispatcher = new WindowsIpcRequestDispatcher(
                handlers,
                validator,
                new WindowsAgentOperationAuthorizer(
                    stateStore,
                    admissionGate,
                    backendOwner,
                    new WindowsIpcOperationAuthorizer(uiCoordinator),
                    localizer!));
            var serverOptions = new WindowsIpcServerOptions(
                IpcPeerRole.Agent,
                new[] { IpcPeerRole.Ui, IpcPeerRole.TestClient },
                IpcCapabilities.Control | IpcCapabilities.Status | IpcCapabilities.UiActivation);
            var sessionFactory = new WindowsIpcServerConnectionSessionFactory(
                serializer,
                dispatcher,
                serverOptions,
                uiCoordinator: uiCoordinator,
                contractValidator: validator);
            var controlServer = new WindowsIpcServerHost(
                new WindowsNamedPipeServer(names.ControlPipeName, new IpcFrameCodec()),
                sessionFactory,
                new WindowsIpcServerHostOptions(maximumActiveConnections: 8));
            var uiProcessLockProbe = new FileProcessInstanceLockProbe(names.UiLockFilePath);
            var processLifetimeCoordinator = new WindowsAgentProcessLifetimeCoordinator(
                commandLine.LaunchMode,
                uiCoordinator,
                backgroundSyncCoordinator,
                uiProcessLockProbe,
                shutdownCoordinator);
            host = new WindowsAgentHost(
                processLock,
                uiProcessLockProbe,
                admissionGate,
                controlServer,
                endpointHost,
                backendOwner,
                backgroundSyncCoordinator,
                lifecycleTransitions,
                trayController,
                uiOpenService,
                uiCloseService,
                uiCoordinator,
                shutdownCoordinator,
                stateStore,
                localizer!,
                processLifetimeCoordinator: processLifetimeCoordinator,
                applicationPreferencesReloadCoordinator: preferencesReloadCoordinator);

            applicationLoop.Run(
                host,
                stateStore,
                trayText.StartupFailureTitle,
                trayText.StartupFailureMessage);
            exitCode = applicationLoop.ShellFailed
                ? WindowsAgentExitCode.ShellFailure
                : WindowsAgentExitCode.Success;
        }
        catch (Exception exception)
        {
            Trace.TraceError($"The Agent composition root failed: {exception.GetType().Name}");
            TryShowStartupFailure(localizer, bootstrapLanguage);
            exitCode = WindowsAgentExitCode.ShellFailure;
        }
        finally
        {
            if (preferencesReloadCoordinator is not null)
            {
                try
                {
                    preferencesReloadCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    Trace.TraceError($"The Agent preference reload coordinator could not stop: {exception.GetType().Name}");
                    exitCode = WindowsAgentExitCode.ShellFailure;
                }
            }

            if (host is not null)
            {
                try
                {
                    host.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    Trace.TraceError($"The Agent host could not finish final disposal: {exception.GetType().Name}");
                    ShowShutdownFailure();
                    exitCode = WindowsAgentExitCode.ShellFailure;
                }
            }
            else
            {
                try
                {
                    processLock.Dispose();
                }
                catch (Exception exception)
                {
                    Trace.TraceError($"The Agent process lock could not be released: {exception.GetType().Name}");
                    exitCode = WindowsAgentExitCode.ShellFailure;
                }
            }

            try
            {
                applicationLoop?.Dispose();
            }
            catch (Exception exception)
            {
                Trace.TraceError($"The Agent native loop could not be disposed: {exception.GetType().Name}");
                exitCode = WindowsAgentExitCode.ShellFailure;
            }
        }

        return (int)exitCode;
    }

    private static void TryShowStartupFailure(
        IAgentLocalizer? localizer,
        AppLanguage selectedLanguage)
    {
        try
        {
            if (localizer is null || localizer.CurrentLanguage != selectedLanguage)
            {
                try
                {
                    localizer = AgentLocalizer.CreateAsync(selectedLanguage)
                        .GetAwaiter()
                        .GetResult();
                }
                catch when (localizer is not null)
                {
                    // Keep the last fully validated bootstrap dictionary if the newly selected
                    // resource cannot be loaded during an early-startup failure path.
                }
            }
            WindowsNativeMessageBox.ShowError(
                localizer!.GetString(AgentLocalizationKeys.StartupFailureTitle),
                localizer.GetString(AgentLocalizationKeys.StartupFailureMessage));
        }
        catch (Exception exception)
        {
            Trace.TraceError($"The localized Agent startup failure could not be displayed: {exception.GetType().Name}");
        }
    }

}
