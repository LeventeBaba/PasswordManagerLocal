using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Frontend.Firewall;

namespace PasswordManagerLocal.Windows.Tests.IPC.Firewall;

[TestClass]
public sealed class WindowsFirewallArchitectureTests
{
    [TestMethod]
    public void ElevatedModeExitsBeforeAnyUserDataInstanceAgentOrUiStartup()
    {
        var source = Read("Windows", "Frontend", "Program.cs");
        var helper = source.IndexOf("Firewall.WindowsFirewallSetup.Run()", StringComparison.Ordinal);
        Assert.IsTrue(helper >= 0);
        foreach (var marker in new[] { "new WindowsApplicationDataPathProvider()", "new FileProcessInstanceLock(",
                     "new WindowsAgentControlConnection(", "BuildAvaloniaApp(frontendContext)" })
            Assert.IsTrue(helper < source.IndexOf(marker, StringComparison.Ordinal), marker);
        StringAssert.Contains(source, "args.Length == 1");
        StringAssert.Contains(source, "Firewall.WindowsFirewallSetup.InvalidArgumentsExitCode");
        StringAssert.Contains(source, "Environment.ExitCode");
    }

    [TestMethod]
    public void OldSavedStatusCannotBypassLiveFirewallChecks()
    {
        var source = Read("Common", "Frontend", "Services", "FirewallPermissionStartupPrompt.cs");
        Assert.IsFalse(source.Contains("WindowsFirewallConfigurationStore.IsConfigured()", StringComparison.Ordinal));
        StringAssert.Contains(source, "await CheckFirewallAsync(ct)");
    }

    [TestMethod]
    public void ResetToolRecognizesEveryOwnedRuleAndNeverChangesGlobalPolicy()
    {
        var source = Read("Tools", "ResetWindowsFirewall.ps1");
        StringAssert.Contains(source, WindowsFirewallConfigurationService.RuleGroup);
        foreach (var suffix in new[] { ".Tcp.In", ".Udp.In", ".Tcp.Out", ".Udp.Out" })
            StringAssert.Contains(source, "$ruleGroup" + suffix);
        StringAssert.Contains(source, "SupportsShouldProcess = $true");
        StringAssert.Contains(source, "$PSCmdlet.ShouldProcess(");
        StringAssert.Contains(source, "-PolicyStore PersistentStore");
        StringAssert.Contains(source, "-PolicyStore ActiveStore");
        StringAssert.Contains(source, "#Requires -RunAsAdministrator");
        Assert.IsFalse(source.Contains("Set-NetFirewallProfile", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(source.Contains("netsh", StringComparison.OrdinalIgnoreCase));
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { GetRepositoryRoot() }.Concat(parts).ToArray()));

    private static string GetRepositoryRoot([CallerFilePath] string source = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
