using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Frontend.AgentConnection;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Ui;

[TestClass]
public sealed class WindowsAgentControlConnectionTests
{
    [TestMethod]
    public async Task ExistingAgentIsReusedWithoutLaunch()
    {
        var identity = CreateIdentity();
        var connector = new FakeWindowsAgentControlConnector();
        connector.Enqueue(new FakeWindowsAgentRegisteredConnection());
        var launcher = new FakeWindowsAgentLauncher();
        await using var connection = new WindowsAgentControlConnection(
            "control-pipe",
            identity,
            launcher,
            connector,
            retryDelay: TimeSpan.Zero);

        Assert.IsTrue(await connection.ConnectAsync());
        Assert.IsTrue(await connection.ConnectAsync());

        Assert.AreEqual(1, connector.AttemptCount);
        Assert.AreEqual(0, launcher.LaunchCount);
        Assert.AreSame(identity, connector.Identities.Single());
    }

    [TestMethod]
    public async Task MissingAgentIsLaunchedThenUiRegistersWithSameIdentity()
    {
        var identity = CreateIdentity();
        var connector = new FakeWindowsAgentControlConnector();
        connector.Enqueue(null);
        connector.Enqueue(new FakeWindowsAgentRegisteredConnection());
        var launcher = new FakeWindowsAgentLauncher();
        await using var connection = new WindowsAgentControlConnection(
            "control-pipe",
            identity,
            launcher,
            connector,
            maximumConnectionAttempts: 3,
            retryDelay: TimeSpan.Zero);

        Assert.IsTrue(await connection.ConnectAsync());

        Assert.AreEqual(1, launcher.LaunchCount);
        Assert.AreEqual(2, connector.AttemptCount);
        Assert.IsTrue(connector.Identities.All(item => ReferenceEquals(item, identity)));
        Assert.AreEqual(1L, connection.ConnectionGeneration);
    }

    [TestMethod]
    public async Task ReconnectAfterLossKeepsIdentityAndAdvancesGeneration()
    {
        var identity = CreateIdentity();
        var first = new FakeWindowsAgentRegisteredConnection();
        var second = new FakeWindowsAgentRegisteredConnection();
        var connector = new FakeWindowsAgentControlConnector();
        connector.Enqueue(first);
        connector.Enqueue(second);
        var processExitWaiter = new FakeWindowsAgentProcessExitWaiter
        {
            Completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var connection = new WindowsAgentControlConnection(
            "control-pipe",
            identity,
            new FakeWindowsAgentLauncher(),
            connector,
            retryDelay: TimeSpan.Zero,
            processExitWaiter: processExitWaiter);
        Assert.IsTrue(await connection.ConnectAsync());
        first.Disconnect();

        Assert.IsTrue(await connection.EnsureConnectedAsync());

        Assert.AreEqual(2L, connection.ConnectionGeneration);
        Assert.AreEqual(2, connector.AttemptCount);
        Assert.IsTrue(connector.Identities.All(item => ReferenceEquals(item, identity)));
        Assert.AreEqual(1, first.DisposeCount);
        Assert.AreEqual(0, processExitWaiter.WaitCount);
    }

    [TestMethod]
    public async Task LostKnownAgentCannotLaunchReplacementBeforeExactProcessExit()
    {
        var first = new FakeWindowsAgentRegisteredConnection { AgentProcessId = 6300 };
        var replacement = new FakeWindowsAgentRegisteredConnection { AgentProcessId = 6301 };
        var connector = new FakeWindowsAgentControlConnector();
        connector.Enqueue(first);
        connector.Enqueue(null);
        connector.Enqueue(null);
        connector.Enqueue(null);
        connector.Enqueue(replacement);
        var launcher = new FakeWindowsAgentLauncher();
        var exitCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processExitWaiter = new FakeWindowsAgentProcessExitWaiter
        {
            Completion = exitCompletion
        };
        await using var connection = new WindowsAgentControlConnection(
            "control-pipe",
            CreateIdentity(),
            launcher,
            connector,
            maximumConnectionAttempts: 2,
            retryDelay: TimeSpan.Zero,
            processExitWaiter: processExitWaiter);
        Assert.IsTrue(await connection.ConnectAsync());
        first.Disconnect();

        var recovery = connection.EnsureConnectedAsync();
        await WaitUntilAsync(() => connector.AttemptCount == 4);

        Assert.IsFalse(recovery.IsCompleted);
        Assert.AreEqual(0, launcher.LaunchCount);
        Assert.AreEqual(6300, processExitWaiter.ProcessId);

        exitCompletion.TrySetResult(true);
        Assert.IsTrue(await recovery);
        Assert.AreEqual(0, launcher.LaunchCount);
        Assert.AreEqual(5, connector.AttemptCount);
        Assert.AreEqual(2L, connection.ConnectionGeneration);
        Assert.AreEqual(6301, connection.AgentProcessId);
    }

    [TestMethod]
    public async Task RetryAfterLaunchIsBounded()
    {
        var connector = new FakeWindowsAgentControlConnector();
        var launcher = new FakeWindowsAgentLauncher();
        await using var connection = new WindowsAgentControlConnection(
            "control-pipe",
            CreateIdentity(),
            launcher,
            connector,
            maximumConnectionAttempts: 3,
            retryDelay: TimeSpan.Zero);

        Assert.IsFalse(await connection.ConnectAsync());

        Assert.AreEqual(1, launcher.LaunchCount);
        Assert.AreEqual(4, connector.AttemptCount);
    }

    [TestMethod]
    public async Task AgentLaunchFailureLeavesConnectionUnavailable()
    {
        var connector = new FakeWindowsAgentControlConnector();
        var launcher = new FakeWindowsAgentLauncher { LaunchResult = false };
        await using var connection = new WindowsAgentControlConnection(
            "control-pipe",
            CreateIdentity(),
            launcher,
            connector,
            retryDelay: TimeSpan.Zero);

        Assert.IsFalse(await connection.ConnectAsync());

        Assert.IsFalse(connection.IsConnected);
        Assert.AreEqual(1, connector.AttemptCount);
        Assert.AreEqual(1, launcher.LaunchCount);
    }

    [TestMethod]
    public async Task StatusAndDatabaseResetUseCurrentControlConnection()
    {
        var registered = new FakeWindowsAgentRegisteredConnection
        {
            DatabaseResetResult = new DatabaseResetResultDto(true, false, null)
        };
        var connector = new FakeWindowsAgentControlConnector();
        connector.Enqueue(registered);
        await using var connection = new WindowsAgentControlConnection(
            "control-pipe",
            CreateIdentity(),
            new FakeWindowsAgentLauncher(),
            connector,
            retryDelay: TimeSpan.Zero);
        Assert.IsTrue(await connection.ConnectAsync());

        var agentStatus = await connection.GetAgentStatusAsync();
        var status = await connection.GetBackendRuntimeStatusAsync();
        var reset = await connection.ResetDatabaseAsync();
        var reloaded = await connection.ReloadApplicationPreferencesAsync();

        Assert.AreEqual(AgentState.Running, agentStatus.AgentState);
        Assert.AreEqual(BackendRuntimeStatusState.Ready, status.RuntimeState);
        Assert.IsTrue(reset.Completed);
        Assert.IsTrue(reloaded);
        Assert.AreEqual(1, registered.ReloadApplicationPreferencesCount);
        Assert.AreEqual(registered.AgentProcessId, connection.AgentProcessId);
    }

    [TestMethod]
    public async Task UnhealthyObservedAgentCannotTriggerEarlyReplacementLaunch()
    {
        var connector = new FakeWindowsAgentControlConnector();
        connector.EnqueueObservedUnavailable(7000);
        connector.Enqueue(null);
        connector.Enqueue(null);
        connector.Enqueue(null);
        var launcher = new FakeWindowsAgentLauncher();
        var processExitWaiter = new FakeWindowsAgentProcessExitWaiter { Result = false };
        await using var connection = new WindowsAgentControlConnection(
            "control-pipe",
            CreateIdentity(),
            launcher,
            connector,
            maximumConnectionAttempts: 2,
            retryDelay: TimeSpan.Zero,
            processExitWaiter: processExitWaiter);

        Assert.IsFalse(await connection.ConnectAsync());

        Assert.AreEqual(0, launcher.LaunchCount);
        Assert.AreEqual(7000, processExitWaiter.ProcessId);
        Assert.AreEqual(7000, connection.AgentProcessId);
    }

    [TestMethod]
    public async Task HealthyReplacementConnectsAfterUnhealthyObservedAgentExits()
    {
        var replacement = new FakeWindowsAgentRegisteredConnection { AgentProcessId = 7001 };
        var connector = new FakeWindowsAgentControlConnector();
        connector.EnqueueObservedUnavailable(7000);
        connector.Enqueue(null);
        connector.Enqueue(replacement);
        var exitCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processExitWaiter = new FakeWindowsAgentProcessExitWaiter
        {
            Completion = exitCompletion
        };
        var launcher = new FakeWindowsAgentLauncher();
        await using var connection = new WindowsAgentControlConnection(
            "control-pipe",
            CreateIdentity(),
            launcher,
            connector,
            maximumConnectionAttempts: 1,
            retryDelay: TimeSpan.Zero,
            processExitWaiter: processExitWaiter);

        var connecting = connection.ConnectAsync();
        await WaitUntilAsync(() => processExitWaiter.WaitCount == 1);
        Assert.AreEqual(0, launcher.LaunchCount);
        exitCompletion.TrySetResult(true);

        Assert.IsTrue(await connecting);
        Assert.AreEqual(7001, connection.AgentProcessId);
        Assert.AreEqual(0, launcher.LaunchCount);
    }

    [TestMethod]
    public async Task ReplacementPreparationWaitsForOldProcessBeforeNewLaunchOrConnect()
    {
        var first = new FakeWindowsAgentRegisteredConnection { AgentProcessId = 6200 };
        var second = new FakeWindowsAgentRegisteredConnection { AgentProcessId = 6201 };
        var connector = new FakeWindowsAgentControlConnector();
        connector.Enqueue(first);
        connector.Enqueue(second);
        var launcher = new FakeWindowsAgentLauncher();
        var exitCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processExitWaiter = new FakeWindowsAgentProcessExitWaiter
        {
            Completion = exitCompletion
        };
        await using var connection = new WindowsAgentControlConnection(
            "control-pipe",
            CreateIdentity(),
            launcher,
            connector,
            retryDelay: TimeSpan.Zero,
            replacementExitTimeout: TimeSpan.FromSeconds(4),
            processExitWaiter: processExitWaiter);
        Assert.IsTrue(await connection.ConnectAsync());

        var preparation = connection.PrepareForReplacementAsync();
        await WaitUntilAsync(() => processExitWaiter.WaitCount == 1);

        Assert.IsFalse(preparation.IsCompleted);
        Assert.AreEqual(1, connector.AttemptCount);
        Assert.AreEqual(0, launcher.LaunchCount);
        Assert.AreEqual(1, first.DisposeCount);
        Assert.AreEqual(6200, processExitWaiter.ProcessId);

        exitCompletion.TrySetResult(true);
        Assert.IsTrue(await preparation);
        Assert.IsTrue(await connection.EnsureConnectedAsync());
        Assert.AreEqual(2, connector.AttemptCount);
    }

    [TestMethod]
    public async Task ReplacementPreparationTimeoutDoesNotLaunchReplacement()
    {
        var connector = new FakeWindowsAgentControlConnector();
        connector.Enqueue(new FakeWindowsAgentRegisteredConnection());
        var launcher = new FakeWindowsAgentLauncher();
        var processExitWaiter = new FakeWindowsAgentProcessExitWaiter { Result = false };
        await using var connection = new WindowsAgentControlConnection(
            "control-pipe",
            CreateIdentity(),
            launcher,
            connector,
            retryDelay: TimeSpan.Zero,
            processExitWaiter: processExitWaiter);
        Assert.IsTrue(await connection.ConnectAsync());

        Assert.IsFalse(await connection.PrepareForReplacementAsync());

        Assert.AreEqual(1, connector.AttemptCount);
        Assert.AreEqual(0, launcher.LaunchCount);
    }

    [TestMethod]
    public async Task DisposalUnregistersPersistentConnectionOnce()
    {
        var connector = new FakeWindowsAgentControlConnector();
        var registered = new FakeWindowsAgentRegisteredConnection();
        connector.Enqueue(registered);
        var connection = new WindowsAgentControlConnection(
            "control-pipe",
            CreateIdentity(),
            new FakeWindowsAgentLauncher(),
            connector,
            retryDelay: TimeSpan.Zero);
        Assert.IsTrue(await connection.ConnectAsync());

        await connection.DisposeAsync();
        await connection.DisposeAsync();

        Assert.AreEqual(1, registered.DisposeCount);
        Assert.IsFalse(connection.IsConnected);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static WindowsUiIpcIdentity CreateIdentity() =>
        new(100, 2, Guid.NewGuid());
}
