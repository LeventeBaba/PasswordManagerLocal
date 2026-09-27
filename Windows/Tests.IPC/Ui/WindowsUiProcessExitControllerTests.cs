using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Frontend.Activation;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using PasswordManagerLocal.Windows.Frontend.Lifecycle;

namespace PasswordManagerLocal.Windows.Tests.IPC.Ui;

[TestClass]
public sealed class WindowsUiProcessExitControllerTests
{
    [TestMethod]
    public async Task WindowExitReleasesOwnershipBeforeAsynchronousCleanupAndTerminates()
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
        var exitCodes = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new WindowsUiProcessExitController(
            activationServer,
            processLock,
            backendClient,
            new WindowsUiProcessShutdownCoordinator(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1)),
            code => exitCodes.TrySetResult(code));

        controller.RequestExit(17);

        Assert.IsTrue(processLock.IsDisposed);
        Assert.AreEqual(17, await exitCodes.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await controller.Completion.WaitAsync(TimeSpan.FromSeconds(2));
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
    public async Task HangingCleanupStillTerminatesTheUiProcess()
    {
        var neverCompletes = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new FakeWindowsIpcServerHost
        {
            StopTaskOverride = neverCompletes.Task
        };
        var activationServer = new WindowsUiActivationServer(host);
        var processLock = new FakeProcessInstanceLock();
        using var disposeEntered = new ManualResetEventSlim();
        using var disposeBlocker = new ManualResetEventSlim();
        var backendClient = new FakeAsyncDisposable
        {
            DisposeEntered = disposeEntered,
            DisposeBlocker = disposeBlocker
        };
        var exitCodes = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new WindowsUiProcessExitController(
            activationServer,
            processLock,
            backendClient,
            new WindowsUiProcessShutdownCoordinator(
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(20)),
            code => exitCodes.TrySetResult(code));

        controller.RequestExit();

        Assert.AreEqual(0, await exitCodes.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(processLock.IsDisposed);
        Assert.IsTrue(disposeEntered.IsSet);
        disposeBlocker.Set();
    }

    [TestMethod]
    public async Task RepeatedExitRequestsTerminateOnlyOnce()
    {
        var host = new FakeWindowsIpcServerHost();
        var activationServer = new WindowsUiActivationServer(host);
        var processLock = new FakeProcessInstanceLock();
        var backendClient = new FakeAsyncDisposable();
        var terminationCount = 0;
        var terminated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new WindowsUiProcessExitController(
            activationServer,
            processLock,
            backendClient,
            terminateProcess: _ =>
            {
                if (Interlocked.Increment(ref terminationCount) == 1)
                    terminated.TrySetResult();
            });

        controller.RequestExit();
        controller.RequestExit();

        await terminated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await controller.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, terminationCount);
        Assert.AreEqual(1, host.StopCount);
        Assert.AreEqual(1, backendClient.DisposeCount);
    }
}
