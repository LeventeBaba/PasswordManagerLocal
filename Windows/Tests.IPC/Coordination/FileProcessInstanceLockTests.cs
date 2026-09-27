using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Coordination;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Coordination;

[TestClass]
public sealed class FileProcessInstanceLockTests
{
    [TestMethod]
    public void FirstAgentOwnershipSucceedsAndSecondIsUnavailable()
    {
        var path = CreateLockPath("agent");
        try
        {
            using var first = new FileProcessInstanceLock(path);
            using var second = new FileProcessInstanceLock(path);

            Assert.IsTrue(first.IsOwner);
            Assert.IsFalse(second.IsOwner);
            Assert.ThrowsExactly<ProcessInstanceAlreadyOwnedException>(second.EnsureOwnership);
        }
        finally
        {
            DeleteRoot(path);
        }
    }

    [TestMethod]
    public void FirstUiOwnershipSucceedsAndSecondIsUnavailable()
    {
        var path = CreateLockPath("ui");
        try
        {
            using var first = new FileProcessInstanceLock(path);
            using var second = new FileProcessInstanceLock(path);

            Assert.IsTrue(first.IsOwner);
            Assert.IsFalse(second.IsOwner);
        }
        finally
        {
            DeleteRoot(path);
        }
    }

    [TestMethod]
    public void AgentAndUiOwnershipDoNotConflict()
    {
        var root = CreateRoot();
        try
        {
            using var agent = new FileProcessInstanceLock(Path.Combine(root, "agent.lock"));
            using var ui = new FileProcessInstanceLock(Path.Combine(root, "ui.lock"));

            Assert.IsTrue(agent.IsOwner);
            Assert.IsTrue(ui.IsOwner);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void OwnershipIsReleasedAfterDisposal()
    {
        var path = CreateLockPath("release");
        try
        {
            using (var first = new FileProcessInstanceLock(path))
                Assert.IsTrue(first.IsOwner);

            using var second = new FileProcessInstanceLock(path);
            Assert.IsTrue(second.IsOwner);
        }
        finally
        {
            DeleteRoot(path);
        }
    }

    [TestMethod]
    public void LockFileRemainsWhileOwnershipHandleIsUsed()
    {
        var path = CreateLockPath("ordinary-use");
        try
        {
            using (var instanceLock = new FileProcessInstanceLock(path))
            {
                instanceLock.EnsureOwnership();
                Assert.IsTrue(File.Exists(path));
                Assert.AreEqual(Path.GetFullPath(path), instanceLock.LockFilePath);
            }

            Assert.IsTrue(File.Exists(path));
        }
        finally
        {
            DeleteRoot(path);
        }
    }

    [TestMethod]
    public void RepeatedDisposalIsSafe()
    {
        var path = CreateLockPath("repeat-dispose");
        try
        {
            var instanceLock = new FileProcessInstanceLock(path);

            instanceLock.Dispose();
            instanceLock.Dispose();

            Assert.ThrowsExactly<ObjectDisposedException>(instanceLock.EnsureOwnership);
        }
        finally
        {
            DeleteRoot(path);
        }
    }

    [TestMethod]
    [Timeout(2_000)]
    public void OwnershipAcquisitionFailureCompletesWithOriginalException()
    {
        var path = CreateLockPath("failure");
        var expected = new UnauthorizedAccessException("denied");
        var opener = new DelegateProcessInstanceLockFileOpener(_ => throw expected);

        var observed = Assert.ThrowsExactly<UnauthorizedAccessException>(
            () => new FileProcessInstanceLock(path, opener));

        Assert.AreSame(expected, observed);
        DeleteRoot(path);
    }

    [TestMethod]
    public void UnavailableOwnershipDoesNotPartiallyAcquireAHandle()
    {
        var path = CreateLockPath("unavailable");
        var opener = new DelegateProcessInstanceLockFileOpener(_ => null);
        using var instanceLock = new FileProcessInstanceLock(path, opener);

        Assert.IsFalse(instanceLock.IsOwner);
        Assert.ThrowsExactly<ProcessInstanceAlreadyOwnedException>(instanceLock.EnsureOwnership);
        DeleteRoot(path);
    }


    [TestMethod]
    public void FailedHandleDisposalLeavesOwnershipHandleStronglyReferenced()
    {
        var path = CreateLockPath("failed-dispose");
        var stream = new ThrowingDisposeStream(new IOException("close failed"));
        var instanceLock = new FileProcessInstanceLock(
            path,
            new DelegateProcessInstanceLockFileOpener(_ => stream));

        Assert.ThrowsExactly<IOException>(instanceLock.Dispose);

        var field = typeof(FileProcessInstanceLock).GetField(
            "_ownershipHandle",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        Assert.AreSame(stream, field.GetValue(instanceLock));
        Assert.AreEqual(1, stream.DisposeCallCount);
        DeleteRoot(path);
    }

    [TestMethod]
    public void NonOwnerCanAcquireAfterPreviousOwnerReleasesLock()
    {
        var path = CreateLockPath("reacquire");
        try
        {
            using var first = new FileProcessInstanceLock(path);
            using var contender = new FileProcessInstanceLock(path);
            Assert.IsTrue(first.IsOwner);
            Assert.IsFalse(contender.IsOwner);

            first.Dispose();

            Assert.IsTrue(contender.TryAcquire());
            Assert.IsTrue(contender.IsOwner);
            contender.EnsureOwnership();
        }
        finally
        {
            DeleteRoot(path);
        }
    }

    [TestMethod]
    public void NonOwningProbeDistinguishesFreeAndHeldUiLock()
    {
        var path = CreateLockPath("ui-probe");
        try
        {
            using (var owner = new FileProcessInstanceLock(path))
            {
                var held = new FileProcessInstanceLockProbe(path).Probe();
                Assert.AreEqual(ProcessInstanceLockProbeResult.Held, held);
                Assert.IsTrue(owner.IsOwner);
            }

            var free = new FileProcessInstanceLockProbe(path).Probe();
            Assert.AreEqual(ProcessInstanceLockProbeResult.Free, free);
        }
        finally
        {
            DeleteRoot(path);
        }
    }

    [TestMethod]
    public void NonOwningProbeReportsUncertainWithoutTakingOwnership()
    {
        var path = CreateLockPath("ui-probe-failure");
        var probe = new FileProcessInstanceLockProbe(
            path,
            new DelegateProcessInstanceLockFileOpener(
                _ => throw new UnauthorizedAccessException("denied")));

        Assert.AreEqual(ProcessInstanceLockProbeResult.Uncertain, probe.Probe());
        DeleteRoot(path);
    }

    private static string CreateLockPath(string role) =>
        Path.Combine(CreateRoot(), $"{role}.lock");

    private static string CreateRoot() =>
        Path.Combine(Path.GetTempPath(), "PasswordManagerLocal.Ipc.Test", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string lockFilePath)
    {
        var root = Path.GetDirectoryName(lockFilePath)!;
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
