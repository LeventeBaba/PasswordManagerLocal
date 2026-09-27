using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Windows.Agent.DatabaseReset;
using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Windows.Agent.Lifecycle;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Agent;

[TestClass]
public sealed class WindowsAgentDatabaseResetCoordinatorTests
{
    [TestMethod]
    public async Task ResetSuspendsBackgroundBeforeEndpointAndRestoresAfterRuntimeReset()
    {
        var operations = new List<string>();
        var endpoint = new FakeWindowsAgentEndpointHost { OperationLog = operations };
        var backend = new FakeWindowsAgentBackendRuntimeOwner
        {
            OperationLog = operations,
            Snapshot = CreateResettableSnapshot()
        };
        var background = new FakeWindowsBackgroundSyncCoordinator
        {
            OperationLog = operations,
            State = FakeWindowsBackgroundSyncCoordinator.OperationalState()
        };
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = new WindowsAgentDatabaseResetCoordinator(
            endpoint,
            backend,
            new WindowsAgentShutdownCoordinator(),
            background,
            transitions,
            AgentLocalizationTestFactory.CreateEnglish());

        var result = await coordinator.ResetAsync();

        Assert.IsTrue(result.Completed);
        CollectionAssert.AreEqual(
            new[]
            {
                "background-suspend",
                "endpoint-stop",
                "backend-reset",
                "background-restore",
                "endpoint-start"
            },
            operations);
        Assert.AreEqual(true, background.LastRestoreEnabled);
        Assert.IsFalse(coordinator.IsResetting);
    }

    [TestMethod]
    public async Task ResetWithBackgroundDisabledDoesNotRestoreLease()
    {
        var background = new FakeWindowsBackgroundSyncCoordinator();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateCoordinator(
            new FakeWindowsAgentEndpointHost(),
            new FakeWindowsAgentBackendRuntimeOwner { Snapshot = CreateResettableSnapshot() },
            background,
            transitions);

        var result = await coordinator.ResetAsync();

        Assert.IsTrue(result.Completed);
        Assert.AreEqual(false, background.LastRestoreEnabled);
        Assert.IsFalse(background.State.IsBackgroundLeaseActive);
    }

    [TestMethod]
    public async Task ConcurrentResetIsRejectedWhileFirstResetOwnsTheTransition()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endpoint = new FakeWindowsAgentEndpointHost { StopRelease = release };
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateCoordinator(
            endpoint,
            new FakeWindowsAgentBackendRuntimeOwner { Snapshot = CreateResettableSnapshot() },
            new FakeWindowsBackgroundSyncCoordinator(),
            transitions);

        var first = coordinator.ResetAsync();
        await WaitUntilAsync(() => coordinator.IsResetting);
        var second = await coordinator.ResetAsync();
        release.TrySetResult();
        var firstResult = await first;

        Assert.IsTrue(firstResult.Completed);
        Assert.IsFalse(second.Completed);
        Assert.IsFalse(second.RequiresProcessRestart);
        Assert.AreEqual("A database reset is already in progress.", second.SafeMessage);
    }

    [TestMethod]
    public async Task UiCancellationAfterEndpointShutdownDoesNotAbortAuthoritativeReset()
    {
        var stopRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var endpoint = new FakeWindowsAgentEndpointHost { StopRelease = stopRelease };
        var backend = new FakeWindowsAgentBackendRuntimeOwner
        {
            Snapshot = CreateResettableSnapshot()
        };
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateCoordinator(
            endpoint,
            backend,
            new FakeWindowsBackgroundSyncCoordinator(),
            transitions);
        using var cancellation = new CancellationTokenSource();

        var reset = coordinator.ResetAsync(cancellation.Token);
        await WaitUntilAsync(() => endpoint.StopCount == 1);
        cancellation.Cancel();
        stopRelease.TrySetResult();
        var result = await reset;

        Assert.IsTrue(result.Completed);
        Assert.AreEqual(1, backend.ResetCount);
        Assert.AreEqual(1, endpoint.StartCount);
    }

    [TestMethod]
    public async Task ResetFailureRequiresRestartAndDoesNotRestoreBackgroundLease()
    {
        var failure = new IOException("database deletion failed");
        var backend = new FakeWindowsAgentBackendRuntimeOwner
        {
            ResetFailure = failure,
            Snapshot = CreateResettableSnapshot()
        };
        var background = new FakeWindowsBackgroundSyncCoordinator
        {
            State = FakeWindowsBackgroundSyncCoordinator.OperationalState()
        };
        var shutdown = new WindowsAgentShutdownCoordinator();
        WindowsAgentShutdownReason? shutdownReason = null;
        shutdown.ShutdownRequested += (_, args) => shutdownReason = args.Reason;
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = new WindowsAgentDatabaseResetCoordinator(
            new FakeWindowsAgentEndpointHost(),
            backend,
            shutdown,
            background,
            transitions,
            AgentLocalizationTestFactory.CreateEnglish());

        var result = await coordinator.ResetAsync();

        Assert.IsFalse(result.Completed);
        Assert.IsTrue(result.RequiresProcessRestart);
        Assert.AreEqual(1, backend.RequireRestartCount);
        Assert.AreEqual(0, background.RestoreCount);
        Assert.AreEqual(WindowsAgentShutdownReason.RestartRequired, shutdownReason);
    }


    [TestMethod]
    public async Task BackgroundRestoreFailureRequiresRestartAndDoesNotRestartEndpoint()
    {
        var endpoint = new FakeWindowsAgentEndpointHost();
        var backend = new FakeWindowsAgentBackendRuntimeOwner
        {
            Snapshot = CreateResettableSnapshot()
        };
        var background = new FakeWindowsBackgroundSyncCoordinator
        {
            State = FakeWindowsBackgroundSyncCoordinator.OperationalState(),
            RestoreFailure = new IOException("background restore")
        };
        var shutdown = new WindowsAgentShutdownCoordinator();
        WindowsAgentShutdownReason? reason = null;
        shutdown.ShutdownRequested += (_, args) => reason = args.Reason;
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = new WindowsAgentDatabaseResetCoordinator(
            endpoint,
            backend,
            shutdown,
            background,
            transitions,
            AgentLocalizationTestFactory.CreateEnglish());

        var result = await coordinator.ResetAsync();

        Assert.IsFalse(result.Completed);
        Assert.IsTrue(result.RequiresProcessRestart);
        Assert.AreEqual(1, background.RestoreCount);
        Assert.AreEqual(0, endpoint.StartCount);
        Assert.AreEqual(WindowsAgentShutdownReason.RestartRequired, reason);
    }

    [TestMethod]
    public async Task ResetRejectedBeforeDestructiveShutdownLeavesEndpointAvailable()
    {
        var endpoint = new FakeWindowsAgentEndpointHost();
        var backend = new FakeWindowsAgentBackendRuntimeOwner();
        using var transitions = new WindowsAgentLifecycleTransitionCoordinator();
        var coordinator = CreateCoordinator(
            endpoint,
            backend,
            new FakeWindowsBackgroundSyncCoordinator(),
            transitions);

        var result = await coordinator.ResetAsync();

        Assert.IsFalse(result.Completed);
        Assert.IsFalse(result.RequiresProcessRestart);
        Assert.AreEqual(0, endpoint.StopCount);
        Assert.AreEqual(0, backend.ResetCount);
        Assert.IsNotNull(result.SafeMessage);
        StringAssert.Contains(result.SafeMessage, "database compatibility failure");
    }

    private static WindowsAgentDatabaseResetCoordinator CreateCoordinator(
        FakeWindowsAgentEndpointHost endpoint,
        FakeWindowsAgentBackendRuntimeOwner backend,
        FakeWindowsBackgroundSyncCoordinator background,
        WindowsAgentLifecycleTransitionCoordinator transitions) => new(
            endpoint,
            backend,
            new WindowsAgentShutdownCoordinator(),
            background,
            transitions,
            AgentLocalizationTestFactory.CreateEnglish());

    private static PasswordManagerLocal.Windows.Agent.Backend.WindowsAgentBackendOwnerSnapshot CreateResettableSnapshot() =>
        FakeWindowsAgentBackendRuntimeOwner.CreateSnapshot(
            runtimeState: BackendRuntimeState.Failed,
            runtimeFailureKind: BackendRuntimeFailureKind.DatabaseCompatibility,
            failure: new InvalidOperationException("unsupported database"));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
