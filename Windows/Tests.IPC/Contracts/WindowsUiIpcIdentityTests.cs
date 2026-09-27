using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Tests.IPC.Contracts;

[TestClass]
public sealed class WindowsUiIpcIdentityTests
{
    [TestMethod]
    public void IdentityIsImmutableAndRetainsOneProcessInstanceValueSet()
    {
        var instanceId = Guid.NewGuid();
        var identity = new WindowsUiIpcIdentity(123, 4, instanceId);

        Assert.AreEqual(123, identity.ProcessId);
        Assert.AreEqual(4, identity.WindowsSessionId);
        Assert.AreEqual(instanceId, identity.InstanceId);
        Assert.AreEqual(identity, identity with { });
    }

    [TestMethod]
    public void InvalidIdentityValuesAreRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new WindowsUiIpcIdentity(0, 1, Guid.NewGuid()));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new WindowsUiIpcIdentity(1, -1, Guid.NewGuid()));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new WindowsUiIpcIdentity(1, 1, Guid.Empty));
    }
}
