using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Frontend.Activation;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using PasswordManagerLocal.Windows.Frontend.Lifecycle;
using System.Diagnostics;

namespace PasswordManagerLocal.Windows.Tests.IPC.Ui;

[TestClass]
public sealed class WindowsUiProcessShutdownCoordinatorTests
{
    [TestMethod]
    public async Task ShutdownRequestsActivationStopReleasesLockThenDisposesConnections()
    {
        var operations = new List<string>();
        var host = new FakeWindowsIpcServerHost { OperationLog = operations };
        var activationServer = new WindowsUiActivationServer(host);
        var processLock = new FakeProcessInstanceLock { OperationLog = operations };
        var backendClient = new FakeAsyncDisposable
        {
            OperationLog = operations,
            OperationName = "backend-dispose"
        };
        var coordinator = new WindowsUiProcessShutdownCoordinator(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        await coordinator.ShutdownAsync(activationServer, processLock, backendClient);

        CollectionAssert.AreEqual(
            new[]
            {
                "control-stop",
                "lock-release",
                "control-dispose",
                "backend-dispose"
            },
            operations);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task HangingCleanupStageDoesNotBlockRemainingShutdown(bool blockActivation)
    {
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new FakeWindowsIpcServerHost
        {
            StopTaskOverride = blockActivation ? neverCompletes.Task : null
        };
        var activationServer = new WindowsUiActivationServer(host);
        var processLock = new FakeProcessInstanceLock();
        var backendClient = new FakeAsyncDisposable
        {
            DisposeTaskOverride = blockActivation ? null : neverCompletes.Task
        };
        var coordinator = new WindowsUiProcessShutdownCoordinator(
            blockActivation ? TimeSpan.FromMilliseconds(20) : TimeSpan.FromSeconds(1),
            blockActivation ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(20));
        var stopwatch = Stopwatch.StartNew();

        await coordinator.ShutdownAsync(activationServer, processLock, backendClient);

        stopwatch.Stop();
        Assert.IsTrue(processLock.IsDisposed);
        Assert.AreEqual(1, backendClient.DisposeCount);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task CleanupFailuresDoNotPreventLaterShutdownSteps()
    {
        var host = new FakeWindowsIpcServerHost
        {
            StopFailure = new IOException("stop failed")
        };
        var activationServer = new WindowsUiActivationServer(host);
        var processLock = new FakeProcessInstanceLock
        {
            DisposeFailure = new IOException("lock failed")
        };
        var backendClient = new FakeAsyncDisposable
        {
            DisposeFailure = new IOException("backend failed")
        };
        var coordinator = new WindowsUiProcessShutdownCoordinator();

        await coordinator.ShutdownAsync(activationServer, processLock, backendClient);

        Assert.AreEqual(1, host.StopCount);
        Assert.AreEqual(1, backendClient.DisposeCount);
    }
}
