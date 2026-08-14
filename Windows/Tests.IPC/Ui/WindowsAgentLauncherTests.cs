using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Frontend.AgentConnection;
using PasswordManagerLocal.Windows.Ipc.Coordination;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Ui;

[TestClass]
public sealed class WindowsAgentLauncherTests
{
    [TestMethod]
    public async Task UiLaunchUsesSiblingAgentAndExplicitUiRequestedArgument()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Password Manager ő {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var agentPath = Path.Combine(directory, WindowsExecutableNames.AgentExecutableFileName);
        await File.WriteAllBytesAsync(agentPath, Array.Empty<byte>());
        try
        {
            var processLauncher = new FakeWindowsAgentProcessLauncher();
            var launcher = new WindowsAgentLauncher(directory, processLauncher);

            Assert.IsTrue(await launcher.LaunchAsync());
            Assert.AreEqual(1, processLauncher.StartCount);
            Assert.IsNotNull(processLauncher.LastStartInfo);
            Assert.AreEqual(agentPath, processLauncher.LastStartInfo.FileName);
            Assert.AreEqual(directory, processLauncher.LastStartInfo.WorkingDirectory);
            Assert.IsFalse(processLauncher.LastStartInfo.UseShellExecute);
            CollectionAssert.AreEqual(
                new[] { WindowsAgentLaunchArguments.UiRequested },
                processLauncher.LastStartInfo.ArgumentList.ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task LegacyNestedAgentExecutableIsNotUsed()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var legacyDirectory = Path.Combine(directory, "AgentRuntime");
        Directory.CreateDirectory(legacyDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(legacyDirectory, WindowsExecutableNames.AgentExecutableFileName),
            Array.Empty<byte>());
        try
        {
            var processLauncher = new FakeWindowsAgentProcessLauncher();
            var launcher = new WindowsAgentLauncher(directory, processLauncher);

            Assert.IsFalse(await launcher.LaunchAsync());
            Assert.AreEqual(0, processLauncher.StartCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
