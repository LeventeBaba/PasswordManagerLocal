using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Windows.Ipc.Coordination;

namespace PasswordManagerLocal.Windows.Tests.IPC.Agent;

[TestClass]
public sealed class WindowsAgentCommandLineParserTests
{
    [TestMethod]
    public void EmptyCommandLineUsesManualLaunchMode()
    {
        var options = new WindowsAgentCommandLineParser().Parse(Array.Empty<string>());

        Assert.AreEqual(WindowsAgentLaunchMode.Manual, options.LaunchMode);
    }

    [TestMethod]
    public void BackgroundArgumentSelectsBackgroundLaunchWithoutChangingSettings()
    {
        var options = new WindowsAgentCommandLineParser().Parse(
            new[] { WindowsAgentLaunchArguments.Background });

        Assert.AreEqual(WindowsAgentLaunchMode.Background, options.LaunchMode);
    }

    [TestMethod]
    public void UiRequestedArgumentSelectsInteractiveStartupGrace()
    {
        var options = new WindowsAgentCommandLineParser().Parse(
            new[] { WindowsAgentLaunchArguments.UiRequested });

        Assert.AreEqual(WindowsAgentLaunchMode.UiRequested, options.LaunchMode);
    }

    [TestMethod]
    public void UnknownOrCombinedArgumentsAreRejected()
    {
        var parser = new WindowsAgentCommandLineParser();

        Assert.ThrowsExactly<ArgumentException>(() => parser.Parse(new[] { "--enable-background" }));
        Assert.ThrowsExactly<ArgumentException>(() => parser.Parse(new[]
        {
            WindowsAgentLaunchArguments.Background,
            "extra"
        }));
    }
}
