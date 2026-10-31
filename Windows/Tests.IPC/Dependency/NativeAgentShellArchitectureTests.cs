using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Runtime.CompilerServices;

namespace PasswordManagerLocal.Windows.Tests.IPC.Dependency;

[TestClass]
public sealed class NativeAgentShellArchitectureTests
{

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
    public void NativeCallbackSourceDeclaresExceptionFallbackAndResourceCleanup()
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
    public void NativeTraySourceContainsNoPollingOrFileWatchers()
    {
        var traySource = ReadSources(Path.Combine(GetRepositoryRoot(), "Windows", "Agent", "Tray"));

        Assert.IsFalse(traySource.Contains("Task.Delay", StringComparison.Ordinal));
        Assert.IsFalse(traySource.Contains("FileSystemWatcher", StringComparison.Ordinal));
        Assert.IsFalse(traySource.Contains("Timer", StringComparison.Ordinal));
    }

    private static string ReadSources(string directory) =>
        string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

    private static string GetRepositoryRoot([CallerFilePath] string sourceFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", "..", ".."));
}
