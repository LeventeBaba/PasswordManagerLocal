using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Agent.Background;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Background;

[TestClass]
public sealed class WindowsRunStartupRegistrationTests
{
    private static readonly string AgentPath = Path.Combine(
        Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
        "Program Files",
        "PasswordManagerLocal",
        "PasswordManagerLocal.Windows.Agent.exe");

    [TestMethod]
    public async Task RegisterWritesQuotedAbsoluteAgentCommandToCurrentUserRunKey()
    {
        var registry = new FakeWindowsCurrentUserRegistry();
        var command = new WindowsAgentStartupCommand(AgentPath);
        var registration = new WindowsRunStartupRegistration(command, registry);

        await registration.RegisterAsync();
        var state = await registration.ReadAsync();

        Assert.AreEqual(WindowsStartupRegistrationConstants.RunKeyPath, registry.LastKeyPath);
        Assert.AreEqual(WindowsStartupRegistrationConstants.ValueName, registry.LastValueName);
        Assert.AreEqual($"\"{Path.GetFullPath(AgentPath)}\" --background", registry.LastValue);
        Assert.IsTrue(state.EntryExists);
        Assert.IsTrue(state.IsRegistered);
        Assert.AreEqual(1, registry.ValueCount);
    }

    [TestMethod]
    public async Task RepeatedRegistrationKeepsOneStableValue()
    {
        var registry = new FakeWindowsCurrentUserRegistry();
        var registration = CreateRegistration(registry);

        await registration.RegisterAsync();
        await registration.RegisterAsync();

        Assert.AreEqual(1, registry.ValueCount);
        Assert.IsTrue((await registration.ReadAsync()).IsRegistered);
    }

    [TestMethod]
    public async Task RemovingPresentOrAbsentRegistrationIsIdempotent()
    {
        var registry = new FakeWindowsCurrentUserRegistry();
        var registration = CreateRegistration(registry);
        await registration.RegisterAsync();

        await registration.UnregisterAsync();
        await registration.UnregisterAsync();

        var state = await registration.ReadAsync();
        Assert.IsFalse(state.EntryExists);
        Assert.IsFalse(state.IsRegistered);
        Assert.AreEqual(0, registry.ValueCount);
    }

    [TestMethod]
    public async Task ReadBackDetectsMismatchedCommand()
    {
        var registry = new FakeWindowsCurrentUserRegistry();
        registry.SetValue(
            WindowsStartupRegistrationConstants.RunKeyPath,
            WindowsStartupRegistrationConstants.ValueName,
            "\"C:\\Other\\PasswordManagerLocal.Windows.Agent.exe\" --background");
        var registration = CreateRegistration(registry);

        var state = await registration.ReadAsync();

        Assert.IsTrue(state.EntryExists);
        Assert.IsFalse(state.IsRegistered);
    }

    [TestMethod]
    public async Task ReadBackTreatsNonStringRunValueAsExistingMismatch()
    {
        var registry = new FakeWindowsCurrentUserRegistry();
        registry.SetValue(
            WindowsStartupRegistrationConstants.RunKeyPath,
            WindowsStartupRegistrationConstants.ValueName,
            7);
        var registration = CreateRegistration(registry);

        var state = await registration.ReadAsync();

        Assert.IsTrue(state.EntryExists);
        Assert.IsFalse(state.IsRegistered);
        Assert.IsNull(state.Command);
    }

    [TestMethod]
    public async Task RegistryAccessFailureIsPropagatedWithoutRealRegistryAccess()
    {
        var registry = new FakeWindowsCurrentUserRegistry
        {
            WriteFailure = new UnauthorizedAccessException("denied")
        };
        var registration = CreateRegistration(registry);

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
            () => registration.RegisterAsync());
    }


    [TestMethod]
    public void StartupCommandRejectsTheWindowsUiExecutable()
    {
        var uiPath = Path.Combine(
            Path.GetDirectoryName(AgentPath)!,
            "PasswordManagerLocal.exe");

        Assert.ThrowsExactly<ArgumentException>(() =>
            new WindowsAgentStartupCommand(uiPath));
    }

    private static WindowsRunStartupRegistration CreateRegistration(
        FakeWindowsCurrentUserRegistry registry) =>
        new(new WindowsAgentStartupCommand(AgentPath), registry);
}
