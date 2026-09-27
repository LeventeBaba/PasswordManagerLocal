using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Coordination;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Agent;

[TestClass]
public sealed class WindowsAgentProcessLifetimeCoordinatorTests
{
    [TestMethod]
    public async Task UiRequestedLaunchWaitsForRegistrationWhenBackgroundIsDisabled()
    {
        var shutdown = new WindowsAgentShutdownCoordinator();
        WindowsAgentShutdownReason? observed = null;
        shutdown.ShutdownRequested += (_, args) => observed = args.Reason;
        var ui = new SingleUiConnectionCoordinator();
        await using var coordinator = CreateCoordinator(
            WindowsAgentLaunchMode.UiRequested,
            ui,
            FakeWindowsBackgroundSyncCoordinator.DisabledState(),
            shutdown,
            uiRequestedRegistrationTimeout: TimeSpan.FromMilliseconds(80));

        coordinator.Start();
        await Task.Delay(20);
        Assert.IsNull(observed);

        Assert.IsTrue(ui.TryRegister(CreateUiContext(), out _));
        await Task.Delay(100);

        Assert.IsNull(observed);
    }

    [TestMethod]
    public async Task UiRequestedLaunchWithoutRegistrationStopsAfterUiExits()
    {
        var shutdown = new WindowsAgentShutdownCoordinator();
        var completion = ObserveShutdownAsync(shutdown);
        var probe = new FakeProcessInstanceLockProbe
        {
            Result = ProcessInstanceLockProbeResult.Held
        };
        await using var coordinator = CreateCoordinator(
            WindowsAgentLaunchMode.UiRequested,
            new SingleUiConnectionCoordinator(),
            FakeWindowsBackgroundSyncCoordinator.DisabledState(),
            shutdown,
            probe,
            uiRequestedRegistrationTimeout: TimeSpan.FromMilliseconds(20));

        coordinator.Start();
        await Task.Delay(50);
        Assert.IsFalse(completion.IsCompleted);

        probe.Result = ProcessInstanceLockProbeResult.Free;
        var reason = await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(WindowsAgentShutdownReason.NoUiAndBackgroundDisabled, reason);
    }

    [TestMethod]
    public async Task ManualOrBackgroundLaunchWithoutUiStopsWhenBackgroundIsDisabled()
    {
        foreach (var launchMode in new[]
                 {
                     WindowsAgentLaunchMode.Manual,
                     WindowsAgentLaunchMode.Background
                 })
        {
            var shutdown = new WindowsAgentShutdownCoordinator();
            var completion = ObserveShutdownAsync(shutdown);
            await using var coordinator = CreateCoordinator(
                launchMode,
                new SingleUiConnectionCoordinator(),
                FakeWindowsBackgroundSyncCoordinator.DisabledState(),
                shutdown);

            coordinator.Start();
            var reason = await completion.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual(WindowsAgentShutdownReason.NoUiAndBackgroundDisabled, reason);
        }
    }

    [TestMethod]
    public async Task BackgroundEnabledKeepsAgentRunningWithoutUi()
    {
        var shutdown = new WindowsAgentShutdownCoordinator();
        WindowsAgentShutdownReason? observed = null;
        shutdown.ShutdownRequested += (_, args) => observed = args.Reason;
        await using var coordinator = CreateCoordinator(
            WindowsAgentLaunchMode.Background,
            new SingleUiConnectionCoordinator(),
            FakeWindowsBackgroundSyncCoordinator.OperationalState(),
            shutdown);

        coordinator.Start();
        await Task.Delay(80);

        Assert.IsNull(observed);
    }

    [TestMethod]
    public async Task UiCloseStopsAgentOnlyAfterUiProcessLeavesWhenBackgroundIsDisabled()
    {
        var shutdown = new WindowsAgentShutdownCoordinator();
        var completion = ObserveShutdownAsync(shutdown);
        var ui = new SingleUiConnectionCoordinator();
        var context = CreateUiContext();
        Assert.IsTrue(ui.TryRegister(context, out _));
        var probe = new FakeProcessInstanceLockProbe
        {
            Result = ProcessInstanceLockProbeResult.Held
        };
        await using var coordinator = CreateCoordinator(
            WindowsAgentLaunchMode.UiRequested,
            ui,
            FakeWindowsBackgroundSyncCoordinator.DisabledState(),
            shutdown,
            probe);
        coordinator.Start();

        Assert.IsTrue(ui.Unregister(context.ConnectionId));
        await Task.Delay(80);
        Assert.IsFalse(completion.IsCompleted);

        probe.Result = ProcessInstanceLockProbeResult.Free;
        var reason = await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(WindowsAgentShutdownReason.NoUiAndBackgroundDisabled, reason);
    }

    [TestMethod]
    public async Task UiCloseLeavesAgentRunningWhenBackgroundIsEnabled()
    {
        var shutdown = new WindowsAgentShutdownCoordinator();
        WindowsAgentShutdownReason? observed = null;
        shutdown.ShutdownRequested += (_, args) => observed = args.Reason;
        var ui = new SingleUiConnectionCoordinator();
        var context = CreateUiContext();
        Assert.IsTrue(ui.TryRegister(context, out _));
        await using var coordinator = CreateCoordinator(
            WindowsAgentLaunchMode.UiRequested,
            ui,
            FakeWindowsBackgroundSyncCoordinator.OperationalState(),
            shutdown);
        coordinator.Start();

        Assert.IsTrue(ui.Unregister(context.ConnectionId));
        await Task.Delay(80);

        Assert.IsNull(observed);
    }

    [TestMethod]
    public async Task UiCloseWaitsForBackgroundTransitionToFinishBeforeDeciding()
    {
        var shutdown = new WindowsAgentShutdownCoordinator();
        var completion = ObserveShutdownAsync(shutdown);
        var ui = new SingleUiConnectionCoordinator();
        var context = CreateUiContext();
        Assert.IsTrue(ui.TryRegister(context, out _));
        var background = new FakeWindowsBackgroundSyncCoordinator
        {
            State = FakeWindowsBackgroundSyncCoordinator.DisabledState() with
            {
                IsTransitionInProgress = true,
                Consistency = WindowsBackgroundSyncConsistency.Transitioning
            }
        };
        await using var coordinator = new WindowsAgentProcessLifetimeCoordinator(
            WindowsAgentLaunchMode.UiRequested,
            ui,
            background,
            new FakeProcessInstanceLockProbe
            {
                Result = ProcessInstanceLockProbeResult.Free
            },
            shutdown,
            uiRequestedRegistrationTimeout: TimeSpan.FromMilliseconds(40),
            uiDisconnectGracePeriod: TimeSpan.FromMilliseconds(10),
            uiPresencePollInterval: TimeSpan.FromMilliseconds(10));
        coordinator.Start();

        Assert.IsTrue(ui.Unregister(context.ConnectionId));
        await Task.Delay(50);
        Assert.IsFalse(completion.IsCompleted);

        background.State = FakeWindowsBackgroundSyncCoordinator.DisabledState();
        var reason = await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(WindowsAgentShutdownReason.NoUiAndBackgroundDisabled, reason);
    }

    [TestMethod]
    public async Task TransientUiReconnectCancelsPendingIdleShutdown()
    {
        var shutdown = new WindowsAgentShutdownCoordinator();
        WindowsAgentShutdownReason? observed = null;
        shutdown.ShutdownRequested += (_, args) => observed = args.Reason;
        var ui = new SingleUiConnectionCoordinator();
        var first = CreateUiContext();
        Assert.IsTrue(ui.TryRegister(first, out _));
        var probe = new FakeProcessInstanceLockProbe
        {
            Result = ProcessInstanceLockProbeResult.Held
        };
        await using var coordinator = CreateCoordinator(
            WindowsAgentLaunchMode.UiRequested,
            ui,
            FakeWindowsBackgroundSyncCoordinator.DisabledState(),
            shutdown,
            probe);
        coordinator.Start();

        Assert.IsTrue(ui.Unregister(first.ConnectionId));
        await Task.Delay(20);
        Assert.IsTrue(ui.TryRegister(CreateUiContext(first.PeerSessionId), out _));
        probe.Result = ProcessInstanceLockProbeResult.Free;
        await Task.Delay(100);

        Assert.IsNull(observed);
    }

    [TestMethod]
    public async Task UncertainUiPresenceIsRetriedUntilTheUiLockIsFree()
    {
        var shutdown = new WindowsAgentShutdownCoordinator();
        var completion = ObserveShutdownAsync(shutdown);
        var ui = new SingleUiConnectionCoordinator();
        var context = CreateUiContext();
        Assert.IsTrue(ui.TryRegister(context, out _));
        var probe = new FakeProcessInstanceLockProbe
        {
            Result = ProcessInstanceLockProbeResult.Uncertain
        };
        await using var coordinator = CreateCoordinator(
            WindowsAgentLaunchMode.UiRequested,
            ui,
            FakeWindowsBackgroundSyncCoordinator.DisabledState(),
            shutdown,
            probe);
        coordinator.Start();

        Assert.IsTrue(ui.Unregister(context.ConnectionId));
        await Task.Delay(50);
        Assert.IsFalse(completion.IsCompleted);

        probe.Result = ProcessInstanceLockProbeResult.Free;
        var reason = await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(WindowsAgentShutdownReason.NoUiAndBackgroundDisabled, reason);
        Assert.IsTrue(probe.ProbeCount > 1);
    }

    [TestMethod]
    public async Task UnavailableBackgroundStateIsRetriedUntilDisabledStateCanBeConfirmed()
    {
        var shutdown = new WindowsAgentShutdownCoordinator();
        var completion = ObserveShutdownAsync(shutdown);
        var background = new FakeWindowsBackgroundSyncCoordinator
        {
            State = FakeWindowsBackgroundSyncCoordinator.DisabledState() with
            {
                Consistency = WindowsBackgroundSyncConsistency.Unavailable
            }
        };
        await using var coordinator = new WindowsAgentProcessLifetimeCoordinator(
            WindowsAgentLaunchMode.Manual,
            new SingleUiConnectionCoordinator(),
            background,
            new FakeProcessInstanceLockProbe
            {
                Result = ProcessInstanceLockProbeResult.Free
            },
            shutdown,
            uiRequestedRegistrationTimeout: TimeSpan.FromMilliseconds(40),
            uiDisconnectGracePeriod: TimeSpan.FromMilliseconds(10),
            uiPresencePollInterval: TimeSpan.FromMilliseconds(10));
        coordinator.Start();

        await Task.Delay(50);
        Assert.IsFalse(completion.IsCompleted);

        background.State = FakeWindowsBackgroundSyncCoordinator.DisabledState();
        var reason = await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(WindowsAgentShutdownReason.NoUiAndBackgroundDisabled, reason);
        Assert.IsTrue(background.ReadCount > 1);
    }

    [TestMethod]
    public async Task TransientBackgroundReadFailureDoesNotDisableFutureIdleEvaluation()
    {
        var shutdown = new WindowsAgentShutdownCoordinator();
        var completion = ObserveShutdownAsync(shutdown);
        var background = new FakeWindowsBackgroundSyncCoordinator
        {
            ReadFailure = new IOException("transient")
        };
        await using var coordinator = new WindowsAgentProcessLifetimeCoordinator(
            WindowsAgentLaunchMode.Manual,
            new SingleUiConnectionCoordinator(),
            background,
            new FakeProcessInstanceLockProbe
            {
                Result = ProcessInstanceLockProbeResult.Free
            },
            shutdown,
            uiRequestedRegistrationTimeout: TimeSpan.FromMilliseconds(40),
            uiDisconnectGracePeriod: TimeSpan.FromMilliseconds(10),
            uiPresencePollInterval: TimeSpan.FromMilliseconds(10));
        coordinator.Start();

        await Task.Delay(50);
        Assert.IsFalse(completion.IsCompleted);

        background.ReadFailure = null;
        var reason = await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(WindowsAgentShutdownReason.NoUiAndBackgroundDisabled, reason);
        Assert.IsTrue(background.ReadCount > 1);
    }

    private static WindowsAgentProcessLifetimeCoordinator CreateCoordinator(
        WindowsAgentLaunchMode launchMode,
        IUiConnectionCoordinator ui,
        WindowsBackgroundSyncStateDto backgroundState,
        WindowsAgentShutdownCoordinator shutdown,
        FakeProcessInstanceLockProbe? probe = null,
        TimeSpan? uiRequestedRegistrationTimeout = null) =>
        new(
            launchMode,
            ui,
            new FakeWindowsBackgroundSyncCoordinator { State = backgroundState },
            probe ?? new FakeProcessInstanceLockProbe
            {
                Result = ProcessInstanceLockProbeResult.Free
            },
            shutdown,
            uiRequestedRegistrationTimeout ?? TimeSpan.FromMilliseconds(40),
            uiDisconnectGracePeriod: TimeSpan.FromMilliseconds(10),
            uiPresencePollInterval: TimeSpan.FromMilliseconds(10));

    private static Task<WindowsAgentShutdownReason> ObserveShutdownAsync(
        WindowsAgentShutdownCoordinator shutdown)
    {
        var completion = new TaskCompletionSource<WindowsAgentShutdownReason>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        shutdown.ShutdownRequested += (_, args) => completion.TrySetResult(args.Reason);
        return completion.Task;
    }

    private static IpcConnectionContext CreateUiContext(Guid? sessionId = null) => new(
        Guid.NewGuid(),
        IpcPeerRole.Ui,
        Environment.ProcessId,
        1,
        sessionId ?? Guid.NewGuid(),
        IpcCapabilities.Control | IpcCapabilities.Status);
}
