using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Frontend.AgentConnection;
using PasswordManagerLocal.Windows.Ipc.Coordination;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.ProcessIntegration;

[TestClass]
[DoNotParallelize]
public sealed class WindowsProcessOwnershipIntegrationTests
{
    [TestMethod]
    [Timeout(20_000)]
    public async Task FirstProcessOwnsLockAndSecondProcessIsRejected()
    {
        EnsureWindows();
        using var directory = new TemporaryDirectory();
        var lockPath = Path.Combine(directory.Path, "agent.lock");
        await using var owner = await ProcessTestHostClient.StartAsync("hold-lock", lockPath);

        using var contender = new FileProcessInstanceLock(lockPath);

        Assert.IsFalse(contender.IsOwner, owner.Diagnostics);
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task ExactOldProcessExitAllowsReplacementOwnership()
    {
        EnsureWindows();
        using var directory = new TemporaryDirectory();
        var lockPath = Path.Combine(directory.Path, "agent.lock");
        await using var owner = await ProcessTestHostClient.StartAsync("hold-lock", lockPath);
        var waiter = new WindowsAgentProcessExitWaiter();

        await owner.SendAsync("exit");
        Assert.IsTrue(
            await waiter.WaitForExitAsync(owner.ProcessId, TimeSpan.FromSeconds(5)),
            owner.Diagnostics);
        using var replacement = new FileProcessInstanceLock(lockPath);

        Assert.IsTrue(replacement.IsOwner, owner.Diagnostics);
    }

    [TestMethod]
    [Timeout(25_000)]
    public async Task FailedCleanupRetainsOwnershipUntilOperatingSystemTerminatesProcess()
    {
        EnsureWindows();
        using var directory = new TemporaryDirectory();
        var lockPath = Path.Combine(directory.Path, "agent.lock");
        await using var owner = await ProcessTestHostClient.StartAsync(
            "failed-dispose-lock",
            lockPath);

        await owner.SendAsync("dispose");
        Assert.AreEqual("DISPOSE_FAILED", await owner.ReadLineAsync(), owner.Diagnostics);
        using (var blockedReplacement = new FileProcessInstanceLock(lockPath))
            Assert.IsFalse(blockedReplacement.IsOwner, owner.Diagnostics);

        await owner.TerminateAsync();
        using var replacement = new FileProcessInstanceLock(lockPath);

        Assert.IsTrue(replacement.IsOwner, owner.Diagnostics);
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task TwoUiProcessesCannotOwnTheSameUiLock()
    {
        EnsureWindows();
        using var directory = new TemporaryDirectory();
        var lockPath = Path.Combine(directory.Path, "ui.lock");
        await using var primary = await ProcessTestHostClient.StartAsync("hold-lock", lockPath);
        await using var secondary = await ProcessTestHostClient.StartAsync(
            "hold-lock",
            lockPath,
            expectedReadyPrefix: "NOT_OWNER");

        Assert.IsTrue(primary.IsRunning, primary.Diagnostics);
        Assert.IsFalse(secondary.IsRunning, secondary.Diagnostics);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("This process-level ownership test requires Windows.");
    }
}
