using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Coordination;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using System.Text.RegularExpressions;

namespace PasswordManagerLocal.Windows.Tests.IPC.Coordination;

[TestClass]
public sealed partial class WindowsInstanceNameProviderTests
{
    [TestMethod]
    public void PerUserResourcesAreStableDistinctAndSanitized()
    {
        var applicationData = CreateApplicationDataPath();
        var provider = new WindowsInstanceNameProvider(
            "Password Manager/Local:Desktop",
            applicationData,
            new FakeWindowsUserIdentityProvider("S-1-5-21-unsafe user@example.test"));

        var first = provider.GetNames();
        var second = provider.GetNames();

        Assert.AreEqual(first, second);
        Assert.AreNotEqual(first.AgentLockFilePath, first.UiLockFilePath);
        Assert.AreNotEqual(first.ControlPipeName, first.UiActivationPipeName);
        Assert.AreNotEqual(first.ControlPipeName, first.EndpointPipeName);
        Assert.AreNotEqual(first.UiActivationPipeName, first.EndpointPipeName);
        Assert.AreNotEqual(first.AgentLockFilePath, first.ControlPipeName);
        Assert.AreNotEqual(first.UiLockFilePath, first.UiActivationPipeName);
        StringAssert.StartsWith(first.AgentLockFilePath, Path.Combine(applicationData, "instance-locks"));
        StringAssert.StartsWith(first.UiLockFilePath, Path.Combine(applicationData, "instance-locks"));
        StringAssert.StartsWith(first.ControlPipeName, "PasswordManagerLocal.Control.");
        StringAssert.StartsWith(first.UiActivationPipeName, "PasswordManagerLocal.UiActivation.");
        StringAssert.StartsWith(first.EndpointPipeName, "PasswordManagerLocal.Endpoints.");
        Assert.IsTrue(SafeLockFileName().IsMatch(Path.GetFileName(first.AgentLockFilePath)));
        Assert.IsTrue(SafeLockFileName().IsMatch(Path.GetFileName(first.UiLockFilePath)));
        Assert.IsTrue(SafeControlPipeName().IsMatch(first.ControlPipeName));
        Assert.IsTrue(SafeActivationPipeName().IsMatch(first.UiActivationPipeName));
        Assert.IsTrue(SafeEndpointPipeName().IsMatch(first.EndpointPipeName));
        Assert.IsFalse(first.AgentLockFilePath.Contains("unsafe", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(first.UiActivationPipeName.Contains('@'));
        Assert.IsFalse(first.EndpointPipeName.Contains('@'));
        Assert.IsTrue(Path.IsPathFullyQualified(first.AgentLockFilePath));
        Assert.IsTrue(Path.IsPathFullyQualified(first.UiLockFilePath));
        Assert.IsFalse(first.AgentLockFilePath.StartsWith("Local\\", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(first.UiLockFilePath.StartsWith("Local\\", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void DifferentUsersReceiveDifferentOwnershipAndPipeIdentifiers()
    {
        var applicationData = CreateApplicationDataPath();
        var first = new WindowsInstanceNameProvider(
            "PasswordManagerLocal",
            applicationData,
            new FakeWindowsUserIdentityProvider("S-1-5-21-100")).GetNames();
        var second = new WindowsInstanceNameProvider(
            "PasswordManagerLocal",
            applicationData,
            new FakeWindowsUserIdentityProvider("S-1-5-21-200")).GetNames();

        Assert.AreNotEqual(first.AgentLockFilePath, second.AgentLockFilePath);
        Assert.AreNotEqual(first.UiLockFilePath, second.UiLockFilePath);
        Assert.AreNotEqual(first.ControlPipeName, second.ControlPipeName);
        Assert.AreNotEqual(first.UiActivationPipeName, second.UiActivationPipeName);
        Assert.AreNotEqual(first.EndpointPipeName, second.EndpointPipeName);
    }

    [TestMethod]
    public void RelativeApplicationDataPathIsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new WindowsInstanceNameProvider(
                "PasswordManagerLocal",
                "relative-data",
                new FakeWindowsUserIdentityProvider("S-1-5-21-100")));
    }

    private static string CreateApplicationDataPath() =>
        Path.Combine(Path.GetTempPath(), "PasswordManagerLocal", Guid.NewGuid().ToString("N"));

    [GeneratedRegex("^(agent|ui)\\.[a-f0-9]{32}\\.lock$")]
    private static partial Regex SafeLockFileName();

    [GeneratedRegex("^PasswordManagerLocal\\.Control\\.[a-f0-9]{32}$")]
    private static partial Regex SafeControlPipeName();

    [GeneratedRegex("^PasswordManagerLocal\\.UiActivation\\.[a-f0-9]{32}$")]
    private static partial Regex SafeActivationPipeName();

    [GeneratedRegex("^PasswordManagerLocal\\.Endpoints\\.[a-f0-9]{32}$")]
    private static partial Regex SafeEndpointPipeName();
}
