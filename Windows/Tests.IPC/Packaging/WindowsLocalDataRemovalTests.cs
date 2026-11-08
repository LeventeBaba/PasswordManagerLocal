using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Frontend.Installation;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Packaging;

[TestClass]
public sealed class WindowsLocalDataRemovalTests
{
    [TestMethod]
    public void RemovalDeletesVaultAndSettingsInsideTheAppDirectory()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "PasswordManagerLocal");
        var names = CreateNames(root);
        var logs = Directory.CreateDirectory(Path.Combine(root, "Logs"));
        File.WriteAllText(Path.Combine(root, "app.db"), "test vault");
        File.WriteAllText(Path.Combine(root, "DbConfig.bin"), "test key");
        File.WriteAllText(Path.Combine(logs.FullName, "app.log"), "test log");
        var neighbor = Path.Combine(temp.Path, "other.txt");
        File.WriteAllText(neighbor, "keep");

        WindowsLocalDataRemoval.Remove(root, names);

        Assert.IsFalse(Directory.Exists(root));
        Assert.AreEqual("keep", File.ReadAllText(neighbor));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RemovalLeavesVaultIntactWhenAgentOrUiOwnsItsLock(bool agentOwnsLock)
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "PasswordManagerLocal");
        var names = CreateNames(root);
        using var owner = new FileProcessInstanceLock(agentOwnsLock ? names.AgentLockFilePath : names.UiLockFilePath);
        Assert.IsTrue(owner.IsOwner);
        var vault = Path.Combine(root, "app.db");
        File.WriteAllText(vault, "keep");

        Assert.ThrowsExactly<InvalidOperationException>(() => WindowsLocalDataRemoval.Remove(root, names));

        Assert.AreEqual("keep", File.ReadAllText(vault));
    }

    [TestMethod]
    public void RemovalLeavesMissingAppDirectoryAbsent()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "PasswordManagerLocal");

        WindowsLocalDataRemoval.Remove(root, CreateNames(root));

        Assert.IsFalse(Directory.Exists(root));
    }

    private static WindowsInstanceNames CreateNames(string directory) => new(
        Path.Combine(directory, "instance-locks", "agent.lock"),
        Path.Combine(directory, "instance-locks", "ui.lock"),
        "control", "activation", "endpoint");
}
