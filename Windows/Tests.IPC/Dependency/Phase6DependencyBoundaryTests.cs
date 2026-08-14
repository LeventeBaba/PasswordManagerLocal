using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Frontend.Activation;
using PasswordManagerLocal.Windows.Agent.Hosting;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace PasswordManagerLocal.Windows.Tests.IPC.Dependency;

[TestClass]
public sealed class Phase6DependencyBoundaryTests
{
    [TestMethod]
    public void AgentReferencesBackendCompositionAndEndpointRpcButNoFrontendOrAvalonia()
    {
        var references = typeof(WindowsAgentHost).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.IsTrue(references.Contains("PasswordManagerLocal.Common.Backend"));
        Assert.IsTrue(references.Contains("PasswordManagerLocal.Common.Backend.Hosting"));
        Assert.IsTrue(references.Contains("PasswordManagerLocal.Windows.Backend"));
        Assert.IsTrue(references.Contains("PasswordManagerLocal.Windows.EndpointRpc.Server"));
        Assert.IsFalse(references.Contains("PasswordManagerLocal.Windows.EndpointRpc.Client"));
        Assert.IsFalse(references.Any(name => name.StartsWith("Avalonia", StringComparison.Ordinal)));
        Assert.IsFalse(references.Contains("PasswordManagerLocal.Common.Frontend"));
    }


    [TestMethod]
    public void AgentEndpointHostWiresTheSingleAuthoritativeConnectionLimit()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Windows",
            "Agent",
            "Endpoint",
            "WindowsAgentEndpointHost.cs"));

        StringAssert.Contains(
            source,
            "new WindowsIpcServerHostOptions(MaximumActiveEndpointConnections)");
        StringAssert.Contains(
            source,
            "public const int MaximumActiveEndpointConnections = 1");
    }

    [TestMethod]
    public void WindowsUiProductionSourceHasNoBackendRuntimeOrInProcessFallback()
    {
        var root = GetRepositoryRoot();
        var windowsDirectory = Path.Combine(root, "Windows", "Frontend");
        var source = ReadSources(windowsDirectory);
        var project = File.ReadAllText(Path.Combine(
            windowsDirectory,
            "PasswordManagerLocal.Windows.Frontend.csproj"));

        Assert.IsFalse(source.Contains("WindowsBackendRuntimeFactory", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("BackendRuntimeLifetimeCoordinator", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("InProcessFrontendBackendClient", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("new BackendRuntime", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("WindowsNamedPipeFrontendBackendClient", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("WindowsNamedPipeEndpointRpcConnector", StringComparison.Ordinal));
        Assert.IsFalse(project.Contains("PasswordManagerLocal.Common.Backend.Hosting.csproj", StringComparison.Ordinal));
        Assert.IsFalse(project.Contains("PasswordManagerLocal.Windows.Backend.csproj", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AgentIsOnlyWindowsProductionCallerOfRuntimeFactory()
    {
        var root = GetRepositoryRoot();
        var productionSources = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !ContainsTestDirectory(path) &&
                !ContainsGeneratedDirectory(path))
            .ToArray();
        var callers = productionSources
            .Where(path => File.ReadAllText(path).Contains(
                "WindowsBackendRuntimeFactory.Create",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { Path.Combine("Windows", "Agent", "Backend", "WindowsAgentBackendRuntimeOwner.cs") },
            callers);
    }

    [TestMethod]
    public void AndroidUsesServiceOwnedInProcessRuntimeComposition()
    {
        var root = GetRepositoryRoot();
        var androidSource = ReadSources(Path.Combine(
            root,
            "Android",
            "Frontend"));

        Assert.IsTrue(androidSource.Contains("PasswordManagerBackgroundService", StringComparison.Ordinal));
        Assert.IsTrue(androidSource.Contains("AndroidServiceFrontendBackendClient", StringComparison.Ordinal));
        Assert.IsFalse(androidSource.Contains("new InProcessFrontendBackendClient", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WindowsUiStartupOrdersLockIdentityControlEndpointAndFrontendContext()
    {
        var program = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Windows",
            "Frontend",
            "Program.cs"));
        var lockIndex = program.IndexOf("names.UiLockFilePath", StringComparison.Ordinal);
        var primaryIndex = program.IndexOf("WindowsUiInstanceRole.Primary", StringComparison.Ordinal);
        var identityIndex = program.IndexOf("new WindowsUiIpcIdentity", StringComparison.Ordinal);
        var controlIndex = program.IndexOf("agentConnection.ConnectAsync", StringComparison.Ordinal);
        var endpointIndex = program.IndexOf("backendClient.ConnectAsync", StringComparison.Ordinal);
        var contextIndex = program.IndexOf("new FrontendApplicationContext", StringComparison.Ordinal);

        Assert.IsTrue(lockIndex >= 0);
        Assert.IsTrue(primaryIndex > lockIndex);
        Assert.IsTrue(identityIndex > primaryIndex);
        Assert.IsTrue(controlIndex > identityIndex);
        Assert.IsTrue(endpointIndex > controlIndex);
        Assert.IsTrue(contextIndex > endpointIndex);
    }

    [TestMethod]
    public void WindowsUiReusesOneIdentityForControlAndEndpointConnectors()
    {
        var program = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Windows",
            "Frontend",
            "Program.cs"));

        Assert.AreEqual(1, CountOccurrences(program, "new WindowsUiIpcIdentity"));
        Assert.IsTrue(Regex.IsMatch(
            program,
            @"names\.ControlPipeName,\s*identity,",
            RegexOptions.CultureInvariant));
        Assert.IsTrue(Regex.IsMatch(
            program,
            @"names\.EndpointPipeName,\s*identity",
            RegexOptions.CultureInvariant));
        Assert.IsFalse(program.Contains("identity.ProcessId", StringComparison.Ordinal));
        Assert.IsFalse(program.Contains("identity.InstanceId", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WindowsUiShutdownRequestsActivationStopReleasesLockAndBoundsBackendCleanup()
    {
        var root = GetRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(
            root,
            "Windows",
            "Frontend",
            "Program.cs"));
        var shutdown = File.ReadAllText(Path.Combine(
            root,
            "Windows",
            "Frontend",
            "Lifecycle",
            "WindowsUiProcessShutdownCoordinator.cs"));
        var client = File.ReadAllText(Path.Combine(
            root,
            "Windows",
            "EndpointRpc",
            "Client",
            "WindowsNamedPipeFrontendBackendClient.cs"));

        var activationStopIndex = shutdown.IndexOf(
            "activationServer.RequestStop()",
            StringComparison.Ordinal);
        var lockDisposeIndex = shutdown.IndexOf(
            "processLock.Dispose()",
            activationStopIndex,
            StringComparison.Ordinal);
        var activationDisposeIndex = shutdown.IndexOf(
            "activationServer.DisposeAsync()",
            lockDisposeIndex,
            StringComparison.Ordinal);
        var clientDisposeIndex = shutdown.IndexOf(
            "backendClient.DisposeAsync()",
            activationDisposeIndex,
            StringComparison.Ordinal);
        var lifetimeCancelIndex = client.IndexOf(
            "_lifetimeSource.Cancel()",
            StringComparison.Ordinal);
        var endpointDisposeIndex = client.IndexOf(
            "await DisposeEndpointConnectionLockedAsync()",
            lifetimeCancelIndex,
            StringComparison.Ordinal);
        var controlDisposeIndex = client.IndexOf(
            "await _agentConnection.DisposeAsync()",
            endpointDisposeIndex,
            StringComparison.Ordinal);

        StringAssert.Contains(program, "new WindowsUiProcessShutdownCoordinator()");
        StringAssert.Contains(program, ".ShutdownAsync(");
        Assert.IsTrue(activationStopIndex >= 0);
        Assert.IsTrue(lockDisposeIndex > activationStopIndex);
        Assert.IsTrue(activationDisposeIndex > lockDisposeIndex);
        Assert.IsTrue(clientDisposeIndex > activationDisposeIndex);
        StringAssert.Contains(shutdown, "Task.WhenAny(operation, Task.Delay(timeout))");
        Assert.IsTrue(program.Contains(
            "using var uiProcessLock",
            StringComparison.Ordinal));
        Assert.IsTrue(lifetimeCancelIndex >= 0);
        Assert.IsTrue(endpointDisposeIndex > lifetimeCancelIndex);
        Assert.IsTrue(controlDisposeIndex > endpointDisposeIndex);
        Assert.IsFalse(program.Contains(
            "BackendLifetimeReason.BackgroundSync",
            StringComparison.Ordinal));
        Assert.IsFalse(program.Contains(
            "WindowsBackendRuntimeFactory",
            StringComparison.Ordinal));
        Assert.IsFalse(program.Contains(
            "new BackendRuntime",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void WindowsUiShutdownDoesNotOwnOrDisposeAgentRuntime()
    {
        var root = GetRepositoryRoot();
        var windowsDirectory = Path.Combine(root, "Windows", "Frontend");
        var source = ReadSources(windowsDirectory);

        Assert.IsFalse(source.Contains("IBackendRuntime", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("BackendRuntimeLifetimeCoordinator", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("AcquireAsync(BackendLifetimeReason.BackgroundSync", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("InProcessFrontendBackendClient", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WindowsBackgroundSettingUsesAgentIpcWithoutDirectFileOrRegistryOwnership()
    {
        var root = GetRepositoryRoot();
        var windowsDirectory = Path.Combine(
            root,
            "Windows",
            "Frontend");
        var source = ReadSources(windowsDirectory);
        var program = File.ReadAllText(Path.Combine(windowsDirectory, "Program.cs"));
        var settingsView = File.ReadAllText(Path.Combine(
            root,
            "Common",
            "Frontend",
            "Views",
            "Settings",
            "SettingsView.axaml"));

        StringAssert.Contains(program, "new WindowsAgentBackgroundSyncSettingsClient(agentConnection)");
        StringAssert.Contains(source, "GetBackgroundSyncStateAsync");
        StringAssert.Contains(source, "SetBackgroundSyncEnabledAsync");
        StringAssert.Contains(settingsView, "IsBackgroundSyncToggleEnabled");
        Assert.IsFalse(source.Contains("FileBackgroundSyncSettingsStore", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("Registry.CurrentUser", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("BackgroundSyncSettingsFileName", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DatabaseResetClearsSqlitePoolsAfterRuntimeDisposalAndBeforeDeletion()
    {
        var root = GetRepositoryRoot();
        var runtime = File.ReadAllText(Path.Combine(
            root,
            "Common",
            "Backend.Hosting",
            "BackendRuntime.cs"));
        var cleaner = File.ReadAllText(Path.Combine(
            root,
            "Common",
            "Backend.Hosting",
            "BackendStorageCleaner.cs"));

        Assert.AreEqual(2, CountOccurrences(runtime, "_storageCleaner.ClearSqlitePools()"));
        Assert.AreEqual(2, CountOccurrences(runtime, "_storageCleaner.DeleteDatabaseFiles()"));
        var resetDisposeIndex = runtime.IndexOf("await DisposeCurrentHostCoreAsync()", StringComparison.Ordinal);
        var resetClearIndex = runtime.IndexOf("_storageCleaner.ClearSqlitePools()", resetDisposeIndex, StringComparison.Ordinal);
        var resetDeleteIndex = runtime.IndexOf("_storageCleaner.DeleteDatabaseFiles()", resetClearIndex, StringComparison.Ordinal);

        Assert.IsTrue(resetDisposeIndex >= 0);
        Assert.IsTrue(resetClearIndex > resetDisposeIndex);
        Assert.IsTrue(resetDeleteIndex > resetClearIndex);
        StringAssert.Contains(cleaner, "SqliteConnection.ClearAllPools()");
    }

    [TestMethod]
    public void DatabaseResetUiControlsRecoverOnSuccessAndFailure()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Common",
            "Frontend",
            "ViewModels",
            "MainViewModel.cs"));
        var actionIndex = source.IndexOf(
            "private async Task HandleDatabaseRecoveryPrimaryActionAsync()",
            StringComparison.Ordinal);
        var finallyIndex = source.IndexOf("finally", actionIndex, StringComparison.Ordinal);
        var resetFlagIndex = source.IndexOf(
            "_isResettingDatabase = false",
            finallyIndex,
            StringComparison.Ordinal);
        var propertyRefreshIndex = source.IndexOf(
            "RaiseDatabaseRecoveryProperties()",
            resetFlagIndex,
            StringComparison.Ordinal);

        Assert.IsTrue(actionIndex >= 0);
        Assert.IsTrue(finallyIndex > actionIndex);
        Assert.IsTrue(resetFlagIndex > finallyIndex);
        Assert.IsTrue(propertyRefreshIndex > resetFlagIndex);
    }

    [TestMethod]
    public void AgentEntryPointReturnsShellFailureWhenFinalHostDisposalFails()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Windows",
            "Agent",
            "Program.cs"));
        var disposeIndex = source.IndexOf(
            "host.DisposeAsync().AsTask().GetAwaiter().GetResult()",
            StringComparison.Ordinal);
        var catchIndex = source.IndexOf("catch", disposeIndex, StringComparison.Ordinal);
        var failureExitIndex = source.IndexOf(
            "exitCode = WindowsAgentExitCode.ShellFailure",
            catchIndex,
            StringComparison.Ordinal);
        var returnIndex = source.LastIndexOf("return (int)exitCode", StringComparison.Ordinal);

        Assert.IsTrue(disposeIndex >= 0);
        Assert.IsTrue(catchIndex > disposeIndex);
        Assert.IsTrue(failureExitIndex > catchIndex);
        Assert.IsTrue(returnIndex > failureExitIndex);
        StringAssert.Contains(source, "ShowShutdownFailure();");
    }

    [TestMethod]
    public void WindowsUiAssemblyRetainsFrontendActivationAndIpcOnly()
    {
        var references = typeof(AvaloniaWindowActivationBridge).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.IsTrue(references.Contains("PasswordManagerLocal.Common.Frontend"));
        Assert.IsTrue(references.Contains("PasswordManagerLocal.Common.Contracts"));
        Assert.IsTrue(references.Contains("PasswordManagerLocal.Windows.EndpointRpc.Client"));
        Assert.IsTrue(references.Contains("PasswordManagerLocal.Windows.Ipc"));
        Assert.IsFalse(references.Contains("PasswordManagerLocal.Windows.EndpointRpc.Server"));
        Assert.IsFalse(references.Contains("PasswordManagerLocal.Common.Backend"));
        Assert.IsFalse(references.Contains("PasswordManagerLocal.Common.Backend.Hosting"));
        Assert.IsFalse(references.Contains("PasswordManagerLocal.Windows.Backend"));
    }


    [TestMethod]
    public void AgentIsTheOnlyWindowsProductionBackgroundSettingWriter()
    {
        var root = GetRepositoryRoot();
        var productionSources = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !ContainsTestDirectory(path) &&
                !ContainsGeneratedDirectory(path))
            .ToArray();
        var windowsWriters = productionSources
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Frontend{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(
                "new FileBackgroundSyncSettingsStore",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { Path.Combine("Windows", "Agent", "Program.cs") },
            windowsWriters);
    }

    [TestMethod]
    public void ProductionStartupRegistrationUsesOnlyCurrentUserRunAndAgentExecutable()
    {
        var root = GetRepositoryRoot();
        var backgroundDirectory = Path.Combine(
            root,
            "Windows",
            "Agent",
            "Background");
        var source = ReadSources(backgroundDirectory);
        var launchArgumentsSource = File.ReadAllText(Path.Combine(
            root,
            "Windows",
            "IPC",
            "Coordination",
            "WindowsAgentLaunchArguments.cs"));

        StringAssert.Contains(source, "Registry.CurrentUser");
        StringAssert.Contains(source, @"Software\Microsoft\Windows\CurrentVersion\Run");
        StringAssert.Contains(source, "PasswordManagerLocal.Agent");
        StringAssert.Contains(source, "PasswordManagerLocal.Windows.Agent.exe");
        StringAssert.Contains(source, "WindowsAgentLaunchArguments.Background");
        StringAssert.Contains(
            launchArgumentsSource,
            "public const string Background = \"--background\";");
        Assert.IsFalse(source.Contains("Registry.LocalMachine", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("HKEY_LOCAL_MACHINE", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("PasswordManagerLocal.exe", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WindowsReleasePackagingPublishesAndMergesIndependentTrimmedOutputs()
    {
        var root = GetRepositoryRoot();
        var frontendProject = File.ReadAllText(Path.Combine(
            root, "Windows", "Frontend", "PasswordManagerLocal.Windows.Frontend.csproj"));
        var agentProject = File.ReadAllText(Path.Combine(
            root, "Windows", "Agent", "PasswordManagerLocal.Windows.Agent.csproj"));
        var packagingScript = File.ReadAllText(Path.Combine(
            root, "Tools", "Windows", "PublishWindowsProduct.ps1"));

        StringAssert.Contains(frontendProject, "ReferenceOutputAssembly=\"false\" Private=\"false\"");
        Assert.IsFalse(frontendProject.Contains("PublishWindowsAgent", StringComparison.Ordinal));
        Assert.IsFalse(frontendProject.Contains("AgentDeploymentDirectoryName", StringComparison.Ordinal));
        foreach (var project in new[] { frontendProject, agentProject })
        {
            StringAssert.Contains(project, "<PublishTrimmed>true</PublishTrimmed>");
            StringAssert.Contains(project, "<TrimMode>partial</TrimMode>");
            StringAssert.Contains(project, "<SelfContained>true</SelfContained>");
            StringAssert.Contains(project, "<PublishSingleFile>false</PublishSingleFile>");
            StringAssert.Contains(project, "<PublishReadyToRun>false</PublishReadyToRun>");
        }
        StringAssert.Contains(packagingScript, @"staging\frontend");
        StringAssert.Contains(packagingScript, @"staging\agent");
        StringAssert.Contains(packagingScript, "--frontend");
        StringAssert.Contains(packagingScript, "--agent");
        StringAssert.Contains(packagingScript, "--output");
        StringAssert.Contains(packagingScript, "--report");
    }

    [TestMethod]
    public void AgentStartupRegistrationFallsBackToTheCurrentApphostDirectory()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Windows",
            "Agent",
            "Program.cs"));

        StringAssert.Contains(source, "Environment.ProcessPath");
        StringAssert.Contains(source, "Path.GetFileName(processPath)");
        StringAssert.Contains(source, "AppContext.BaseDirectory");
        StringAssert.Contains(source, "WindowsStartupRegistrationConstants.AgentExecutableFileName");
    }

    [TestMethod]
    public void AgentOwnsExactlyOneOptionalBackgroundLeaseField()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Windows",
            "Agent",
            "Background",
            "WindowsBackgroundSyncCoordinator.cs"));

        Assert.AreEqual(1, CountOccurrences(source, "IBackendRuntimeLease? _backgroundLease"));
        Assert.AreEqual(1, CountOccurrences(source, "_backendOwner.AcquireBackgroundSyncLeaseAsync"));
        Assert.IsFalse(source.Contains("static IBackendRuntimeLease", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BackgroundControlWriteUsesReadBackInsteadOfAutomaticReplay()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Windows",
            "Frontend",
            "Settings",
            "WindowsAgentBackgroundSyncSettingsClient.cs"));

        Assert.AreEqual(1, CountOccurrences(source, "SetBackgroundSyncEnabledAsync("));
        StringAssert.Contains(source, "ReadBackAfterUncertainWriteAsync");
        StringAssert.Contains(source, "GetBackgroundSyncStateAsync");
        Assert.IsTrue(source.IndexOf(
            "GetBackgroundSyncStateAsync",
            source.IndexOf("ReadBackAfterUncertainWriteAsync", StringComparison.Ordinal),
            StringComparison.Ordinal) >= 0);
    }


    [TestMethod]
    public void WindowsUiReleasesItsInstanceLockBeforeBoundedConnectionCleanup()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "Windows",
            "Frontend",
            "Lifecycle",
            "WindowsUiProcessShutdownCoordinator.cs"));
        var activationStop = source.IndexOf(
            "activationServer.RequestStop()",
            StringComparison.Ordinal);
        var lockRelease = source.IndexOf(
            "processLock.Dispose()",
            activationStop,
            StringComparison.Ordinal);
        var activationCleanup = source.IndexOf(
            "activationServer.DisposeAsync()",
            lockRelease,
            StringComparison.Ordinal);
        var backendCleanup = source.IndexOf(
            "backendClient.DisposeAsync()",
            activationCleanup,
            StringComparison.Ordinal);

        Assert.IsTrue(activationStop >= 0);
        Assert.IsTrue(lockRelease > activationStop);
        Assert.IsTrue(activationCleanup > lockRelease);
        Assert.IsTrue(backendCleanup > activationCleanup);
        StringAssert.Contains(source, "Task.WhenAny(operation, Task.Delay(timeout))");
    }

    [TestMethod]
    public void WindowsUiMainWindowCloseExplicitlyEndsTheDesktopLifetime()
    {
        var repositoryRoot = GetRepositoryRoot();
        var appSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Common",
            "Frontend",
            "App.axaml.cs"));
        var programSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Windows",
            "Frontend",
            "Program.cs"));

        StringAssert.Contains(
            appSource,
            "desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;");
        StringAssert.Contains(
            appSource,
            "mainWindow.Closed += (_, _) =>");
        StringAssert.Contains(
            appSource,
            "DesktopExitRequested?.Invoke();");
        StringAssert.Contains(
            appSource,
            "desktop.TryShutdown();");

        var closeHandler = appSource.IndexOf(
            "mainWindow.Closed += (_, _) =>",
            StringComparison.Ordinal);
        var exitRequest = appSource.IndexOf(
            "DesktopExitRequested?.Invoke();",
            StringComparison.Ordinal);
        var desktopShutdown = appSource.IndexOf(
            "TryShutdownDesktop(desktop);",
            StringComparison.Ordinal);

        Assert.IsTrue(closeHandler >= 0);
        Assert.IsTrue(exitRequest > closeHandler);
        Assert.IsTrue(desktopShutdown > exitRequest);
        StringAssert.Contains(
            programSource,
            "ShutdownMode.OnExplicitShutdown");
        StringAssert.Contains(
            programSource,
            "new WindowsUiProcessExitController(");
        StringAssert.Contains(
            programSource,
            "() => exitController.RequestExit()");
    }

    [TestMethod]
    public void WindowsUiWindowCloseStartsIndependentProcessExitBeforeAvaloniaReturns()
    {
        var repositoryRoot = GetRepositoryRoot();
        var contextSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Common",
            "Frontend",
            "FrontendApplicationContext.cs"));
        var appSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Common",
            "Frontend",
            "App.axaml.cs"));
        var programSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Windows",
            "Frontend",
            "Program.cs"));
        var exitControllerSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Windows",
            "Frontend",
            "Lifecycle",
            "WindowsUiProcessExitController.cs"));
        var shutdownSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Windows",
            "Frontend",
            "Lifecycle",
            "WindowsUiProcessShutdownCoordinator.cs"));

        StringAssert.Contains(contextSource, "Action? desktopExitRequested = null");
        StringAssert.Contains(contextSource, "DesktopExitRequested = desktopExitRequested;");
        StringAssert.Contains(appSource, "DesktopExitRequested?.Invoke();");
        StringAssert.Contains(programSource, "() => exitController.RequestExit()");
        StringAssert.Contains(exitControllerSource, "IsBackground = false");
        StringAssert.Contains(exitControllerSource, "_terminateProcess(exitCode);");
        StringAssert.Contains(exitControllerSource, "StopAcceptanceAndReleaseLock(");
        StringAssert.Contains(shutdownSource, "Task.Factory.StartNew(");
        Assert.IsFalse(appSource.Contains(
            "Dispatcher.UIThread.Post(() => TryShutdownDesktop(desktop))",
            StringComparison.Ordinal));
    }

    private static int CountOccurrences(string value, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private static string ReadSources(string directory) => string.Join(
        Environment.NewLine,
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !ContainsGeneratedDirectory(path))
            .Select(File.ReadAllText));

    private static bool ContainsTestDirectory(string path) =>
        path.Contains(".Test", StringComparison.Ordinal) ||
        path.Contains(
            $"{Path.DirectorySeparatorChar}Tests.",
            StringComparison.Ordinal) ||
        path.Contains(
            $"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal);

    private static bool ContainsGeneratedDirectory(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string GetRepositoryRoot([CallerFilePath] string sourceFilePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourceFilePath)!,
            "..",
            "..",
            ".."));
}
