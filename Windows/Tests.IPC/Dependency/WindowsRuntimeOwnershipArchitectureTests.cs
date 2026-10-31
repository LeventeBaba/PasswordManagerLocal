using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Frontend.Activation;
using PasswordManagerLocal.Windows.Agent.Hosting;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace PasswordManagerLocal.Windows.Tests.IPC.Dependency;

[TestClass]
public sealed class WindowsRuntimeOwnershipArchitectureTests
{
    [TestMethod]
    public void AgentReferencesBackendAndEndpointServerWithoutManagedUiDependencies()
    {
        var references = typeof(WindowsAgentHost).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty).ToArray();
        foreach (var required in new[]
        {
            "PasswordManagerLocal.Common.Backend", "PasswordManagerLocal.Common.Backend.Hosting",
            "PasswordManagerLocal.Windows.Backend", "PasswordManagerLocal.Windows.EndpointRpc.Server"
        })
            CollectionAssert.Contains(references, required);
        foreach (var forbidden in new[]
        {
            "PasswordManagerLocal.Windows.EndpointRpc.Client", "PasswordManagerLocal.Common.Frontend",
            "PasswordManagerLocal.Windows.Frontend", "System.Windows.Forms", "PresentationCore", "PresentationFramework"
        })
            CollectionAssert.DoesNotContain(references, forbidden);
        Assert.IsFalse(references.Any(name => name.StartsWith("Avalonia", StringComparison.Ordinal)
            || name.StartsWith("ReactiveUI", StringComparison.Ordinal)));

        var project = System.Xml.Linq.XDocument.Load(Path.Combine(
            GetRepositoryRoot(), "Windows", "Agent", "PasswordManagerLocal.Windows.Agent.csproj"));
        foreach (var property in new[] { "UseWindowsForms", "UseWPF" })
            Assert.IsFalse(project.Descendants(property).Any(element =>
                string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)), property);
    }

    [TestMethod]
    public void EndpointHostConfiguresOneProductionConnection()
    {
        var source = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "Windows", "Agent", "Endpoint", "WindowsAgentEndpointHost.cs"));
        var declaredLimit = typeof(PasswordManagerLocal.Windows.Agent.Endpoint.WindowsAgentEndpointHost)
            .GetField(nameof(PasswordManagerLocal.Windows.Agent.Endpoint.WindowsAgentEndpointHost.MaximumActiveEndpointConnections))
            ?.GetRawConstantValue();

        Assert.AreEqual(1, (int)(declaredLimit ?? throw new InvalidOperationException("Missing endpoint connection limit.")));
        StringAssert.Contains(source, "new WindowsIpcServerHostOptions(MaximumActiveEndpointConnections)");
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
    public void UiEntryPointWiresShutdownAndEndpointClientCancelsLifetimeBeforeDisposingConnections()
    {
        var root = GetRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "Windows", "Frontend", "Program.cs"));
        var client = File.ReadAllText(Path.Combine(root, "Windows", "EndpointRpc", "Client", "WindowsNamedPipeFrontendBackendClient.cs"));

        StringAssert.Contains(program, "new WindowsUiProcessShutdownCoordinator()");
        StringAssert.Contains(program, ".ShutdownAsync(");
        StringAssert.Contains(program, "using var uiProcessLock");
        var cancel = client.IndexOf("_lifetimeSource.Cancel()", StringComparison.Ordinal);
        Assert.IsTrue(cancel >= 0);
        var endpointDispose = client.IndexOf("await DisposeEndpointConnectionLockedAsync()", cancel, StringComparison.Ordinal);
        Assert.IsTrue(endpointDispose > cancel);
        var controlDispose = client.IndexOf("await _agentConnection.DisposeAsync()", endpointDispose, StringComparison.Ordinal);
        Assert.IsTrue(controlDispose > endpointDispose);
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
        var productionSources = Directory.EnumerateFiles(Path.Combine(root, "Windows"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !ContainsTestDirectory(path) &&
                !ContainsGeneratedDirectory(path))
            .ToArray();
        var windowsWriters = productionSources
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
            "Constants",
            "Launch",
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
    public void DesktopWindowCloseWiresExplicitShutdownAndIndependentProcessExit()
    {
        var root = GetRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "Common", "Frontend", "App.axaml.cs"));
        var context = File.ReadAllText(Path.Combine(root, "Common", "Frontend", "FrontendApplicationContext.cs"));
        var program = File.ReadAllText(Path.Combine(root, "Windows", "Frontend", "Program.cs"));
        var exit = File.ReadAllText(Path.Combine(root, "Windows", "Frontend", "Lifecycle", "WindowsUiProcessExitController.cs"));
        var shutdown = File.ReadAllText(Path.Combine(root, "Windows", "Frontend", "Lifecycle", "WindowsUiProcessShutdownCoordinator.cs"));

        StringAssert.Contains(app, "desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;");
        StringAssert.Contains(app, "desktop.TryShutdown();");
        var closed = app.IndexOf("mainWindow.Closed += (_, _) =>", StringComparison.Ordinal);
        var exitRequest = app.IndexOf("DesktopExitRequested?.Invoke();", StringComparison.Ordinal);
        var desktopShutdown = app.IndexOf("TryShutdownDesktop(desktop);", StringComparison.Ordinal);
        Assert.IsTrue(closed >= 0);
        Assert.IsTrue(exitRequest > closed);
        Assert.IsTrue(desktopShutdown > exitRequest);
        StringAssert.Contains(context, "Action? desktopExitRequested = null");
        StringAssert.Contains(context, "DesktopExitRequested = desktopExitRequested;");
        StringAssert.Contains(program, "ShutdownMode.OnExplicitShutdown");
        StringAssert.Contains(program, "new WindowsUiProcessExitController(");
        StringAssert.Contains(program, "() => exitController.RequestExit()");
        StringAssert.Contains(exit, "IsBackground = false");
        StringAssert.Contains(exit, "_terminateProcess(exitCode);");
        StringAssert.Contains(exit, "StopAcceptanceAndReleaseLock(");
        StringAssert.Contains(shutdown, "Task.Factory.StartNew(");
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
