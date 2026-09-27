using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Agent.Hosting;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace PasswordManagerLocal.Windows.Tests.IPC.Dependency;

[TestClass]
public sealed class Phase4NativeAgentShellArchitectureTests
{
    [TestMethod]
    public void AgentProjectEnablesNoManagedUiFramework()
    {
        var project = XDocument.Load(Path.Combine(
            GetRepositoryRoot(),
            "Windows",
            "Agent",
            "PasswordManagerLocal.Windows.Agent.csproj"));
        var references = project.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();
        var assemblyReferences = typeof(WindowsAgentHost).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.IsFalse(project.Descendants("UseWindowsForms").Any());
        Assert.IsFalse(project.Descendants("UseWPF").Any());
        Assert.IsFalse(assemblyReferences.Contains("System.Windows.Forms"));
        Assert.IsFalse(assemblyReferences.Any(name => name.StartsWith("Avalonia", StringComparison.Ordinal)));
        Assert.IsFalse(references.Any(reference =>
            string.Equals(
                Path.GetFileName(reference),
                "PasswordManagerLocal.Common.Frontend.csproj",
                StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(references.Any(reference =>
            string.Equals(
                Path.GetFileName(reference),
                "PasswordManagerLocal.Windows.Frontend.csproj",
                StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void AgentSourceContainsNoWinFormsShellRemnants()
    {
        var agent = Path.Combine(GetRepositoryRoot(), "Windows", "Agent");
        var source = ReadSources(agent);
        var forbidden = new[]
        {
            "System.Windows.Forms",
            "new NotifyIcon",
            "ContextMenuStrip",
            "ToolStripMenuItem",
            "Application.Run",
            "ApplicationConfiguration.Initialize",
            "WindowsFormsSynchronizationContext",
            "WindowsAgentApplicationContext",
            "WindowsFormsTrayIconAdapter"
        };

        foreach (var value in forbidden)
            Assert.IsFalse(source.Contains(value, StringComparison.Ordinal), value);
        Assert.IsFalse(File.Exists(Path.Combine(agent, "Hosting", "WindowsAgentApplicationContext.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(agent, "Tray", "WindowsFormsTrayIconAdapter.cs")));
    }

    [TestMethod]
    public void NativeShellUsesBlockingMessageLoopAndMessageDrivenDispatch()
    {
        var root = GetRepositoryRoot();
        var window = File.ReadAllText(Path.Combine(
            root, "Windows", "Agent", "Native", "WindowsNativeMessageWindow.cs"));
        var methods = File.ReadAllText(Path.Combine(
            root, "Windows", "Agent", "Native", "WindowsNativeMethods.cs"));
        var dispatcher = File.ReadAllText(Path.Combine(
            root, "Windows", "Agent", "Native", "WindowsNativeShellDispatcher.cs"));
        var nativeShellSource = ReadSources(Path.Combine(root, "Windows", "Agent", "Native"));

        StringAssert.Contains(window, "WindowsNativeMethods.GetMessage");
        StringAssert.Contains(window, "WindowsNativeMethods.TranslateMessage");
        StringAssert.Contains(window, "WindowsNativeMethods.DispatchMessage");
        StringAssert.Contains(methods, "EntryPoint = \"PostMessageW\"");
        StringAssert.Contains(dispatcher, "TryPostMessage(_dispatchMessage)");
        StringAssert.Contains(dispatcher, "Queue<PendingAction>");
        Assert.IsFalse(nativeShellSource.Contains("Task.Delay", StringComparison.Ordinal));
        Assert.IsFalse(nativeShellSource.Contains("Thread.Sleep", StringComparison.Ordinal));
        Assert.IsFalse(nativeShellSource.Contains("Timer", StringComparison.Ordinal));
        Assert.IsFalse(nativeShellSource.Contains("SpinWait", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NativeCallbackBoundaryAndOwnedResourcesHaveExplicitCleanup()
    {
        var root = GetRepositoryRoot();
        var window = File.ReadAllText(Path.Combine(
            root, "Windows", "Agent", "Native", "WindowsNativeMessageWindow.cs"));
        var tray = File.ReadAllText(Path.Combine(
            root, "Windows", "Agent", "Tray", "WindowsNativeTrayIconAdapter.cs"));
        var icon = File.ReadAllText(Path.Combine(
            root, "Windows", "Agent", "Native", "WindowsNativeIcon.cs"));

        StringAssert.Contains(window, "catch (Exception exception)");
        StringAssert.Contains(window, "DefWindowProc");
        StringAssert.Contains(tray, "NimDelete");
        StringAssert.Contains(tray, "DestroyMenu");
        StringAssert.Contains(icon, "DestroyIcon");
        StringAssert.Contains(tray, "TaskbarCreated");
        StringAssert.Contains(tray, "SetForegroundWindow");
    }

    [TestMethod]
    public void TrayVisibilityUsesBackgroundSyncStateWithoutPolling()
    {
        var root = GetRepositoryRoot();
        var host = File.ReadAllText(Path.Combine(
            root, "Windows", "Agent", "Hosting", "WindowsAgentHost.cs"));
        var handler = File.ReadAllText(Path.Combine(
            root, "Windows", "Agent", "Background", "SetBackgroundSyncEnabledWindowsIpcRequestHandler.cs"));
        var traySource = ReadSources(Path.Combine(root, "Windows", "Agent", "Tray"));

        StringAssert.Contains(host, "initialBackgroundSyncState.IsEnabled");
        StringAssert.Contains(handler, "SetVisibleAsync(state.IsEnabled");
        Assert.IsFalse(traySource.Contains("Task.Delay", StringComparison.Ordinal));
        Assert.IsFalse(traySource.Contains("FileSystemWatcher", StringComparison.Ordinal));
        Assert.IsFalse(traySource.Contains("Timer", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AgentReadsLanguageButDoesNotReadTheme()
    {
        var root = GetRepositoryRoot();
        var reader = File.ReadAllText(Path.Combine(
            root, "Windows", "Agent", "Preferences", "WindowsAgentApplicationPreferencesReader.cs"));
        var program = File.ReadAllText(Path.Combine(
            root, "Windows", "Agent", "Program.cs"));
        var text = File.ReadAllText(Path.Combine(
            root, "Windows", "Agent", "Tray", "WindowsAgentTrayText.cs"));
        var english = File.ReadAllText(Path.Combine(
            root, "Windows", "Agent", "Assets", "Localization", "en_us.json"));
        var hungarian = File.ReadAllText(Path.Combine(
            root, "Windows", "Agent", "Assets", "Localization", "hu.json"));

        StringAssert.Contains(reader, "preferences.Language");
        Assert.IsFalse(reader.Contains("Theme", StringComparison.Ordinal));
        StringAssert.Contains(program, "ReadLanguageAsync");
        StringAssert.Contains(text, "IAgentLocalizer");
        StringAssert.Contains(english, "Open PasswordManagerLocal");
        StringAssert.Contains(hungarian, "PasswordManagerLocal megnyitása");
        StringAssert.Contains(hungarian, "Kilépés");
    }

    private static string ReadSources(string directory) =>
        string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

    private static string GetRepositoryRoot([CallerFilePath] string sourceFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", "..", ".."));
}
