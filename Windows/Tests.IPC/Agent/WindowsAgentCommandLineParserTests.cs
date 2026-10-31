using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Windows.Ipc.Coordination;

namespace PasswordManagerLocal.Windows.Tests.IPC.Agent;

[TestClass]
public sealed class WindowsAgentCommandLineParserTests
{
    [TestMethod]
    [DataRow(null, WindowsAgentLaunchMode.Manual)]
    [DataRow(WindowsAgentLaunchArguments.Background, WindowsAgentLaunchMode.Background)]
    [DataRow(WindowsAgentLaunchArguments.UiRequested, WindowsAgentLaunchMode.UiRequested)]
    public void SupportedArgumentsSelectExpectedLaunchMode(string? argument, WindowsAgentLaunchMode expected)
    {
        var arguments = argument is null ? Array.Empty<string>() : new[] { argument! };

        var options = new WindowsAgentCommandLineParser().Parse(arguments);

        Assert.AreEqual(expected, options.LaunchMode);
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
