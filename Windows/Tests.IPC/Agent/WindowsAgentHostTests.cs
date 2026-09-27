using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Agent.Backend;
using PasswordManagerLocal.Windows.Agent.Lifecycle;
using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Windows.Agent.Preferences;
using PasswordManagerLocal.Windows.Agent.Ui;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Coordination;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Agent;

[TestClass]
public sealed class WindowsAgentHostTests
{
    [TestMethod]
    public async Task SuccessfulRegisteredUiExitAcknowledgesBeforeDestructiveCleanupAndReleasesLockLast()
    {
        var operations = new List<string>();
        var processLock = new FakeProcessInstanceLock { OperationLog = operations };
        var control = new FakeWindowsIpcServerHost { OperationLog = operations };
        var endpoint = new FakeWindowsAgentEndpointHost { OperationLog = operations };
        var backend = new FakeWindowsAgentBackendRuntimeOwner { OperationLog = operations };
        var tray = new FakeTrayIconController { OperationLog = operations };
        var close = new FakeWindowsUiCloseService { OperationLog = operations };
        var coordinator = new FakeUiConnectionCoordinator
        {
            Registration = FakeUiConnectionCoordinator.CreateRegistration()
        };
        var state = new WindowsAgentStateStore();
        await using var host = CreateHost(
            processLock, control, endpoint, backend, tray, close: close,
            coordinator: coordinator, state: state);
        await host.StartAsync();
        operations.Clear();

        var result = await host.RequestShutdownAsync(WindowsAgentShutdownReason.UserRequestedExit);

        Assert.AreEqual(WindowsAgentShutdownResultKind.Completed, result.Kind);
        CollectionAssert.AreEqual(
            new[]
            {
                "ui-close-request",
                "endpoint-stop",
                "backend-stop",
                "control-stop",
                "backend-dispose",
                "endpoint-dispose",
                "control-dispose",
                "tray-dispose",
                "lock-release"
            },
            operations);
        Assert.AreEqual(AgentState.Stopped, state.State);
        Assert.IsTrue(processLock.IsDisposed);
        Assert.IsFalse(host.RetainsProcessOwnershipUntilTermination);
    }

    [TestMethod]
    public async Task NoRegisteredUiExitSkipsAcknowledgementAndBlocksLateRegistration()
    {
        var coordinator = new FakeUiConnectionCoordinator();
        var close = new FakeWindowsUiCloseService();
        var processLock = new FakeProcessInstanceLock();
        await using var host = CreateHost(
            processLock,
            new FakeWindowsIpcServerHost(),
            new FakeWindowsAgentEndpointHost(),
            new FakeWindowsAgentBackendRuntimeOwner(),
            new FakeTrayIconController(),
            close: close,
            coordinator: coordinator);
        await host.StartAsync();

        var result = await host.RequestShutdownAsync(WindowsAgentShutdownReason.UserRequestedExit);

        Assert.AreEqual(WindowsAgentShutdownResultKind.Completed, result.Kind);
        Assert.AreEqual(0, close.RequestCount);
        Assert.AreEqual(1, coordinator.BeginShutdownCount);
        Assert.IsTrue(processLock.IsDisposed);
    }

    [TestMethod]
    public async Task NoRegistrationWithHeldUiLockRejectsExitWithoutClosingAdmission()
    {
        var state = new WindowsAgentStateStore();
        var admissionGate = new WindowsAgentAdmissionGate();
        var probe = new FakeProcessInstanceLockProbe
        {
            Result = ProcessInstanceLockProbeResult.Held
        };
        var endpoint = new FakeWindowsAgentEndpointHost();
        await using var host = CreateHost(
            new FakeProcessInstanceLock(),
            new FakeWindowsIpcServerHost(),
            endpoint,
            new FakeWindowsAgentBackendRuntimeOwner(),
            new FakeTrayIconController(),
            state: state,
            uiProcessLockProbe: probe,
            admissionGate: admissionGate,
            uiRegistrationPreflightTimeout: TimeSpan.FromMilliseconds(40),
            uiRegistrationPollInterval: TimeSpan.FromMilliseconds(5));
        await host.StartAsync();

        var result = await host.RequestShutdownAsync(WindowsAgentShutdownReason.UserRequestedExit);

        Assert.AreEqual(WindowsAgentShutdownResultKind.Rejected, result.Kind);
        Assert.AreEqual(AgentState.Running, state.State);
        Assert.IsTrue(admissionGate.IsOpen);
        Assert.AreEqual(0, endpoint.StopCount);
    }

    [TestMethod]
    public async Task HeldUiLockThatReregistersUsesIntentionalShutdownHandshake()
    {
        var coordinator = new FakeUiConnectionCoordinator();
        var close = new FakeWindowsUiCloseService();
        var probe = new FakeProcessInstanceLockProbe
        {
            Result = ProcessInstanceLockProbeResult.Held
        };
        await using var host = CreateHost(
            new FakeProcessInstanceLock(),
            new FakeWindowsIpcServerHost(),
            new FakeWindowsAgentEndpointHost(),
            new FakeWindowsAgentBackendRuntimeOwner(),
            new FakeTrayIconController(),
            close: close,
            coordinator: coordinator,
            uiProcessLockProbe: probe,
            uiRegistrationPreflightTimeout: TimeSpan.FromSeconds(1),
            uiRegistrationPollInterval: TimeSpan.FromMilliseconds(5));
        await host.StartAsync();

        var exit = host.RequestShutdownAsync(WindowsAgentShutdownReason.UserRequestedExit);
        await WaitUntilAsync(() => probe.ProbeCount > 1);
        coordinator.Registration = FakeUiConnectionCoordinator.CreateRegistration();
        var result = await exit;

        Assert.AreEqual(WindowsAgentShutdownResultKind.Completed, result.Kind);
        Assert.AreEqual(1, close.RequestCount);
        Assert.AreEqual(1, coordinator.BeginShutdownCount);
    }

    [TestMethod]
    public async Task UncertainUiPresenceRejectsExitFailClosed()
    {
        var endpoint = new FakeWindowsAgentEndpointHost();
        await using var host = CreateHost(
            new FakeProcessInstanceLock(),
            new FakeWindowsIpcServerHost(),
            endpoint,
            new FakeWindowsAgentBackendRuntimeOwner(),
            new FakeTrayIconController(),
            uiProcessLockProbe: new FakeProcessInstanceLockProbe
            {
                Result = ProcessInstanceLockProbeResult.Uncertain
            });
        await host.StartAsync();

        var result = await host.RequestShutdownAsync(WindowsAgentShutdownReason.UserRequestedExit);

        Assert.AreEqual(WindowsAgentShutdownResultKind.Rejected, result.Kind);
        Assert.AreEqual(0, endpoint.StopCount);
    }

    [TestMethod]
    public async Task AcknowledgementFailureRejectsExitWithoutDestructiveCleanupAndAllowsRetry()
    {
        var processLock = new FakeProcessInstanceLock();
        var endpoint = new FakeWindowsAgentEndpointHost();
        var backend = new FakeWindowsAgentBackendRuntimeOwner();
        var control = new FakeWindowsIpcServerHost();
        var tray = new FakeTrayIconController();
        var close = new FakeWindowsUiCloseService
        {
            Result = new WindowsUiCloseResult(
                WindowsUiCloseResultKind.Failed,
                "Acknowledgement timed out.")
        };
        var coordinator = new FakeUiConnectionCoordinator
        {
            Registration = FakeUiConnectionCoordinator.CreateRegistration()
        };
        var state = new WindowsAgentStateStore();
        var admissionGate = new WindowsAgentAdmissionGate();
        await using var host = CreateHost(
            processLock, control, endpoint, backend, tray,
            close: close, coordinator: coordinator, state: state,
            admissionGate: admissionGate);
        await host.StartAsync();

        var rejected = await host.RequestShutdownAsync(WindowsAgentShutdownReason.UserRequestedExit);

        Assert.AreEqual(WindowsAgentShutdownResultKind.Rejected, rejected.Kind);
        Assert.AreEqual(AgentState.Running, state.State);
        Assert.AreEqual(0, endpoint.StopCount);
        Assert.AreEqual(0, backend.StopCount);
        Assert.AreEqual(0, control.StopCount);
        Assert.AreEqual(0, tray.DisposeCount);
        Assert.IsFalse(processLock.IsDisposed);
        Assert.AreEqual(1, tray.ExitFailureCount);
        Assert.AreEqual(1, coordinator.CancelShutdownCount);
        Assert.IsTrue(admissionGate.IsOpen);

        close.Result = new WindowsUiCloseResult(
            WindowsUiCloseResultKind.Acknowledged,
            "Acknowledged.");
        var completed = await host.RequestShutdownAsync(WindowsAgentShutdownReason.UserRequestedExit);

        Assert.AreEqual(WindowsAgentShutdownResultKind.Completed, completed.Kind);
        Assert.AreEqual(2, close.RequestCount);
        Assert.AreEqual(1, endpoint.StopCount);
        Assert.IsTrue(processLock.IsDisposed);
    }

    [TestMethod]
    public async Task RepeatedTrayExitSharesOneAcknowledgementAndOneShutdown()
    {
        var closeCompletion = new TaskCompletionSource<WindowsUiCloseResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var close = new FakeWindowsUiCloseService { Completion = closeCompletion };
        var endpoint = new FakeWindowsAgentEndpointHost();
        var coordinator = new FakeUiConnectionCoordinator
        {
            Registration = FakeUiConnectionCoordinator.CreateRegistration()
        };
        await using var host = CreateHost(
            new FakeProcessInstanceLock(),
            new FakeWindowsIpcServerHost(),
            endpoint,
            new FakeWindowsAgentBackendRuntimeOwner(),
            new FakeTrayIconController(),
            close: close,
            coordinator: coordinator);
        await host.StartAsync();

        var first = host.RequestShutdownAsync(WindowsAgentShutdownReason.UserRequestedExit);
        var second = host.RequestShutdownAsync(WindowsAgentShutdownReason.UserRequestedExit);
        await WaitUntilAsync(() => close.RequestCount == 1);
        closeCompletion.TrySetResult(new WindowsUiCloseResult(
            WindowsUiCloseResultKind.Acknowledged,
            "Acknowledged."));
        await Task.WhenAll(first, second);

        Assert.AreEqual(1, close.RequestCount);
        Assert.AreEqual(1, endpoint.StopCount);
        Assert.AreEqual(1, coordinator.BeginShutdownCount);
    }

    [TestMethod]
    public async Task ConcurrentRestartRequiredWaitsForTrayExitAcknowledgementPreflight()
    {
        var closeCompletion = new TaskCompletionSource<WindowsUiCloseResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var close = new FakeWindowsUiCloseService { Completion = closeCompletion };
        var endpoint = new FakeWindowsAgentEndpointHost();
        var processLock = new FakeProcessInstanceLock();
        var admissionGate = new WindowsAgentAdmissionGate();
        var coordinator = new FakeUiConnectionCoordinator
        {
            Registration = FakeUiConnectionCoordinator.CreateRegistration()
        };
        await using var host = CreateHost(
            processLock,
            new FakeWindowsIpcServerHost(),
            endpoint,
            new FakeWindowsAgentBackendRuntimeOwner(),
            new FakeTrayIconController(),
            close: close,
            coordinator: coordinator,
            admissionGate: admissionGate);
        await host.StartAsync();

        var trayExit = host.RequestShutdownAsync(WindowsAgentShutdownReason.UserRequestedExit);
        await WaitUntilAsync(() => close.RequestCount == 1);
        var restart = host.RequestShutdownAsync(WindowsAgentShutdownReason.RestartRequired);
        await Task.Delay(25);

        Assert.AreEqual(AgentAdmissionState.Closed, admissionGate.State);
        Assert.AreEqual(0, endpoint.StopCount);

        closeCompletion.TrySetResult(new WindowsUiCloseResult(
            WindowsUiCloseResultKind.Acknowledged,
            "Acknowledged."));
        await Task.WhenAll(trayExit, restart);

        Assert.AreEqual(1, endpoint.StopCount);
        Assert.IsFalse(processLock.IsDisposed);
        Assert.IsTrue(host.RetainsProcessOwnershipUntilTermination);
    }

    [TestMethod]
    public async Task RestartReasonIsReservedBeforeOuterDisposalCanRequestApplicationExit()
    {
        var processLock = new FakeProcessInstanceLock();
        var shutdownCoordinator = new WindowsAgentShutdownCoordinator();
        var host = CreateHost(
            processLock,
            new FakeWindowsIpcServerHost(),
            new FakeWindowsAgentEndpointHost(),
            new FakeWindowsAgentBackendRuntimeOwner(),
            new FakeTrayIconController(),
            shutdownCoordinator: shutdownCoordinator);
        await host.StartAsync();

        shutdownCoordinator.RequestShutdown(WindowsAgentShutdownReason.RestartRequired);
        await host.DisposeAsync();

        Assert.IsFalse(processLock.IsDisposed);
        Assert.IsTrue(host.RetainsProcessOwnershipUntilTermination);
    }

    [TestMethod]
    public async Task EveryCriticalCleanupFailureRetainsProcessOwnership()
    {
        var cases = new (string Name, Action<FakeWindowsAgentEndpointHost, FakeWindowsAgentBackendRuntimeOwner, FakeWindowsIpcServerHost> Configure)[]
        {
            ("endpoint stop", (endpoint, _, _) => endpoint.StopFailure = new IOException("endpoint stop")),
            ("endpoint dispose", (endpoint, _, _) => endpoint.DisposeFailure = new IOException("endpoint dispose")),
            ("backend stop", (_, backend, _) => backend.StopFailure = new IOException("backend stop")),
            ("backend dispose", (_, backend, _) => backend.DisposeFailure = new IOException("backend dispose")),
            ("control stop", (_, _, control) => control.StopFailure = new IOException("control stop")),
            ("control dispose", (_, _, control) => control.DisposeFailure = new IOException("control dispose"))
        };

        foreach (var testCase in cases)
        {
            var processLock = new FakeProcessInstanceLock();
            var endpoint = new FakeWindowsAgentEndpointHost();
            var backend = new FakeWindowsAgentBackendRuntimeOwner();
            var control = new FakeWindowsIpcServerHost();
            testCase.Configure(endpoint, backend, control);
            var state = new WindowsAgentStateStore();
            var admissionGate = new WindowsAgentAdmissionGate();
            var host = CreateHost(
                processLock, control, endpoint, backend,
                new FakeTrayIconController(), state: state,
                admissionGate: admissionGate);
            await host.StartAsync();

            var result = await host.RequestShutdownAsync(WindowsAgentShutdownReason.ApplicationExit);

            Assert.AreEqual(WindowsAgentShutdownResultKind.Failed, result.Kind, testCase.Name);
            Assert.AreEqual(AgentState.Failed, state.State, testCase.Name);
            Assert.IsTrue(state.LastFailure!.RequiresProcessRestart, testCase.Name);
            Assert.IsFalse(processLock.IsDisposed, testCase.Name);
            Assert.IsTrue(host.RetainsProcessOwnershipUntilTermination, testCase.Name);
            Assert.AreEqual(AgentAdmissionState.Closed, admissionGate.State, testCase.Name);
            Assert.ThrowsExactly<InvalidOperationException>(admissionGate.Open);
            await Assert.ThrowsExactlyAsync<IOException>(() => host.DisposeAsync().AsTask());
        }
    }

    [TestMethod]
    public async Task MultipleCriticalFailuresAreAggregatedAndLockRemainsHeld()
    {
        var processLock = new FakeProcessInstanceLock();
        var endpoint = new FakeWindowsAgentEndpointHost
        {
            StopFailure = new IOException("endpoint stop")
        };
        var backend = new FakeWindowsAgentBackendRuntimeOwner
        {
            DisposeFailure = new IOException("backend dispose")
        };
        var host = CreateHost(
            processLock,
            new FakeWindowsIpcServerHost { DisposeFailure = new IOException("control dispose") },
            endpoint,
            backend,
            new FakeTrayIconController());
        await host.StartAsync();

        var result = await host.RequestShutdownAsync(WindowsAgentShutdownReason.ApplicationExit);

        Assert.IsInstanceOfType<AggregateException>(result.Failure);
        Assert.AreEqual(3, ((AggregateException)result.Failure!).InnerExceptions.Count);
        Assert.IsFalse(processLock.IsDisposed);
        Assert.IsTrue(host.RetainsProcessOwnershipUntilTermination);
        await Assert.ThrowsExactlyAsync<AggregateException>(() => host.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task ProcessLockReleaseFailureRetainsOwnershipUntilProcessTermination()
    {
        var lockState = new FakeProcessInstanceLockState();
        var processLock = new FakeProcessInstanceLock(lockState)
        {
            DisposeFailure = new IOException("lock release")
        };
        var host = CreateHost(
            processLock,
            new FakeWindowsIpcServerHost(),
            new FakeWindowsAgentEndpointHost(),
            new FakeWindowsAgentBackendRuntimeOwner(),
            new FakeTrayIconController());
        await host.StartAsync();

        var result = await host.RequestShutdownAsync(WindowsAgentShutdownReason.ApplicationExit);

        Assert.AreEqual(WindowsAgentShutdownResultKind.Failed, result.Kind);
        Assert.IsFalse(processLock.IsDisposed);
        Assert.IsTrue(host.RetainsProcessOwnershipUntilTermination);
        var replacement = new FakeProcessInstanceLock(lockState);
        Assert.IsFalse(replacement.IsOwner);
        processLock.SimulateProcessTermination();
        await Assert.ThrowsExactlyAsync<IOException>(() => host.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task NoncriticalTrayFailureReleasesLockButReportsFailedShellCleanup()
    {
        var processLock = new FakeProcessInstanceLock();
        var state = new WindowsAgentStateStore();
        var host = CreateHost(
            processLock,
            new FakeWindowsIpcServerHost(),
            new FakeWindowsAgentEndpointHost(),
            new FakeWindowsAgentBackendRuntimeOwner(),
            new FakeTrayIconController { DisposeFailure = new IOException("tray dispose") },
            state: state);
        await host.StartAsync();

        var result = await host.RequestShutdownAsync(WindowsAgentShutdownReason.ApplicationExit);

        Assert.AreEqual(WindowsAgentShutdownResultKind.Failed, result.Kind);
        Assert.AreEqual(AgentState.Failed, state.State);
        Assert.IsFalse(state.LastFailure!.RequiresProcessRestart);
        Assert.IsTrue(processLock.IsDisposed);
        Assert.IsFalse(host.RetainsProcessOwnershipUntilTermination);
        await Assert.ThrowsExactlyAsync<IOException>(() => host.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task FailedOldAgentExcludesReplacementUntilProcessTermination()
    {
        var lockState = new FakeProcessInstanceLockState();
        var oldLock = new FakeProcessInstanceLock(lockState);
        var oldHost = CreateHost(
            oldLock,
            new FakeWindowsIpcServerHost(),
            new FakeWindowsAgentEndpointHost(),
            new FakeWindowsAgentBackendRuntimeOwner { StopFailure = new IOException("stop") },
            new FakeTrayIconController());
        await oldHost.StartAsync();
        await oldHost.RequestShutdownAsync(WindowsAgentShutdownReason.ApplicationExit);

        var replacementWhileOldAlive = new FakeProcessInstanceLock(lockState);
        Assert.IsFalse(replacementWhileOldAlive.IsOwner);
        Assert.ThrowsExactly<ProcessInstanceAlreadyOwnedException>(
            replacementWhileOldAlive.EnsureOwnership);

        oldLock.SimulateProcessTermination();
        var replacementAfterTermination = new FakeProcessInstanceLock(lockState);
        replacementAfterTermination.EnsureOwnership();
        Assert.IsTrue(replacementAfterTermination.IsOwner);
        await Assert.ThrowsExactlyAsync<IOException>(() => oldHost.DisposeAsync().AsTask());
        replacementAfterTermination.Dispose();
    }

    [TestMethod]
    public async Task OuterHostDisposalCannotReleaseLockAfterCriticalFailure()
    {
        var processLock = new FakeProcessInstanceLock();
        var host = CreateHost(
            processLock,
            new FakeWindowsIpcServerHost(),
            new FakeWindowsAgentEndpointHost { DisposeFailure = new IOException("dispose") },
            new FakeWindowsAgentBackendRuntimeOwner(),
            new FakeTrayIconController());
        await host.StartAsync();
        await host.RequestShutdownAsync(WindowsAgentShutdownReason.ApplicationExit);

        await Assert.ThrowsExactlyAsync<IOException>(() => host.DisposeAsync().AsTask());

        Assert.IsFalse(processLock.IsDisposed);
        Assert.IsTrue(host.RetainsProcessOwnershipUntilTermination);
    }

    [TestMethod]
    public async Task RestartRequiredShutdownRetainsLockEvenAfterSuccessfulCleanup()
    {
        var processLock = new FakeProcessInstanceLock();
        await using var host = CreateHost(
            processLock,
            new FakeWindowsIpcServerHost(),
            new FakeWindowsAgentEndpointHost(),
            new FakeWindowsAgentBackendRuntimeOwner(),
            new FakeTrayIconController());
        await host.StartAsync();

        var result = await host.RequestShutdownAsync(WindowsAgentShutdownReason.RestartRequired);

        Assert.AreEqual(WindowsAgentShutdownResultKind.Completed, result.Kind);
        Assert.IsFalse(processLock.IsDisposed);
        Assert.IsTrue(host.RetainsProcessOwnershipUntilTermination);
    }


    [TestMethod]
    public async Task FatalLifecycleShutdownRetainsLockUntilProcessTermination()
    {
        var processLock = new FakeProcessInstanceLock();
        await using var host = CreateHost(
            processLock,
            new FakeWindowsIpcServerHost(),
            new FakeWindowsAgentEndpointHost(),
            new FakeWindowsAgentBackendRuntimeOwner(),
            new FakeTrayIconController());
        await host.StartAsync();

        var result = await host.RequestShutdownAsync(WindowsAgentShutdownReason.FatalLifecycleFailure);

        Assert.AreEqual(WindowsAgentShutdownResultKind.Completed, result.Kind);
        Assert.IsFalse(processLock.IsDisposed);
        Assert.IsTrue(host.RetainsProcessOwnershipUntilTermination);
    }

    [TestMethod]
    public async Task NonResettableBackendFailureClosesAdmissionAndTriggersFatalShutdown()
    {
        var processLock = new FakeProcessInstanceLock();
        var state = new WindowsAgentStateStore();
        var admissionGate = new WindowsAgentAdmissionGate();
        var backend = new FakeWindowsAgentBackendRuntimeOwner();
        await using var host = CreateHost(
            processLock,
            new FakeWindowsIpcServerHost(),
            new FakeWindowsAgentEndpointHost(),
            backend,
            new FakeTrayIconController(),
            state: state,
            admissionGate: admissionGate);
        await host.StartAsync();

        backend.PublishSnapshot(FakeWindowsAgentBackendRuntimeOwner.CreateSnapshot(
            ownerState: WindowsAgentBackendOwnerState.Failed,
            runtimeState: BackendRuntimeState.Failed,
            runtimeFailureKind: BackendRuntimeFailureKind.StorageUnavailable,
            failure: new IOException("storage failure")));

        await WaitUntilAsync(() => host.RetainsProcessOwnershipUntilTermination);

        Assert.AreEqual(AgentAdmissionState.Closed, admissionGate.State);
        Assert.IsFalse(processLock.IsDisposed);
    }

    [TestMethod]
    public async Task StartupRollbackFailureRetainsLockUntilProcessTermination()
    {
        var processLock = new FakeProcessInstanceLock();
        var admissionGate = new WindowsAgentAdmissionGate();
        var host = CreateHost(
            processLock,
            new FakeWindowsIpcServerHost(),
            new FakeWindowsAgentEndpointHost(),
            new FakeWindowsAgentBackendRuntimeOwner
            {
                StartFailure = new IOException("startup"),
                DisposeFailure = new IOException("rollback dispose")
            },
            new FakeTrayIconController(),
            admissionGate: admissionGate);

        await Assert.ThrowsExactlyAsync<AggregateException>(() => host.StartAsync());

        Assert.IsFalse(processLock.IsDisposed);
        Assert.IsTrue(host.RetainsProcessOwnershipUntilTermination);
        Assert.AreEqual(AgentAdmissionState.Closed, admissionGate.State);
        Assert.ThrowsExactly<InvalidOperationException>(admissionGate.Open);
        await Assert.ThrowsExactlyAsync<IOException>(() => host.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task SuccessfulStartupFailureRollbackReleasesLock()
    {
        var processLock = new FakeProcessInstanceLock();
        var admissionGate = new WindowsAgentAdmissionGate();
        await using var host = CreateHost(
            processLock,
            new FakeWindowsIpcServerHost(),
            new FakeWindowsAgentEndpointHost(),
            new FakeWindowsAgentBackendRuntimeOwner { StartFailure = new IOException("startup") },
            new FakeTrayIconController(),
            admissionGate: admissionGate);

        await Assert.ThrowsExactlyAsync<IOException>(() => host.StartAsync());

        Assert.IsTrue(processLock.IsDisposed);
        Assert.IsFalse(host.RetainsProcessOwnershipUntilTermination);
        Assert.AreEqual(AgentAdmissionState.Closed, admissionGate.State);
        Assert.ThrowsExactly<InvalidOperationException>(admissionGate.Open);
    }

    [TestMethod]
    public async Task SecondOwnerStartsNoRuntimeOrTransport()
    {
        var processLock = new FakeProcessInstanceLock(isOwner: false);
        var control = new FakeWindowsIpcServerHost();
        var endpoint = new FakeWindowsAgentEndpointHost();
        var backend = new FakeWindowsAgentBackendRuntimeOwner();
        var tray = new FakeTrayIconController();
        await using var host = CreateHost(processLock, control, endpoint, backend, tray);

        await Assert.ThrowsExactlyAsync<ProcessInstanceAlreadyOwnedException>(() => host.StartAsync());

        Assert.AreEqual(0, backend.StartCount);
        Assert.AreEqual(0, control.StartCount);
        Assert.AreEqual(0, endpoint.StartCount);
        Assert.AreEqual(0, tray.InitializeCount);
    }

    [TestMethod]
    public async Task ShutdownDuringStartupCannotTransitionBackToRunning()
    {
        var trayRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tray = new FakeTrayIconController { InitializationRelease = trayRelease };
        var state = new WindowsAgentStateStore();
        var observed = new List<AgentState> { state.State };
        state.StateChanged += (_, args) => observed.Add(args.Current);
        await using var host = CreateHost(
            new FakeProcessInstanceLock(),
            new FakeWindowsIpcServerHost(),
            new FakeWindowsAgentEndpointHost(),
            new FakeWindowsAgentBackendRuntimeOwner(),
            tray,
            state: state);

        var startup = host.StartAsync();
        await WaitUntilAsync(() => tray.InitializeCount == 1);
        var shutdown = host.ShutdownAsync();
        trayRelease.TrySetResult();
        await Task.WhenAll(startup, shutdown);

        Assert.IsFalse(observed.Contains(AgentState.Running));
        Assert.AreEqual(AgentState.Stopped, state.State);
    }


    [TestMethod]
    public async Task StartupRestoresBackgroundStateBeforeControlAndEndpointAcceptance()
    {
        var operations = new List<string>();
        var background = new FakeWindowsBackgroundSyncCoordinator
        {
            State = FakeWindowsBackgroundSyncCoordinator.OperationalState(),
            OperationLog = operations
        };
        var control = new FakeWindowsIpcServerHost { OperationLog = operations };
        var endpoint = new FakeWindowsAgentEndpointHost { OperationLog = operations };
        var backend = new FakeWindowsAgentBackendRuntimeOwner { OperationLog = operations };
        await using var host = CreateHost(
            new FakeProcessInstanceLock { OperationLog = operations },
            control,
            endpoint,
            backend,
            new FakeTrayIconController { OperationLog = operations },
            backgroundSync: background);

        await host.StartAsync();

        Assert.AreEqual(1, background.InitializeCount);
        Assert.IsTrue(operations.IndexOf("backend-start") < operations.IndexOf("background-initialize"));
        Assert.IsTrue(operations.IndexOf("background-initialize") < operations.IndexOf("control-start"));
        Assert.IsTrue(operations.IndexOf("background-initialize") < operations.IndexOf("endpoint-start"));
    }


    [TestMethod]
    public async Task StartupShowsTrayOnlyWhenBackgroundSyncIsEnabled()
    {
        var enabledTray = new FakeTrayIconController();
        await using (var enabledHost = CreateHost(
            new FakeProcessInstanceLock(),
            new FakeWindowsIpcServerHost(),
            new FakeWindowsAgentEndpointHost(),
            new FakeWindowsAgentBackendRuntimeOwner(),
            enabledTray,
            backgroundSync: new FakeWindowsBackgroundSyncCoordinator
            {
                State = FakeWindowsBackgroundSyncCoordinator.OperationalState()
            }))
        {
            await enabledHost.StartAsync();
            Assert.AreEqual(true, enabledTray.LastVisible);
        }

        var disabledTray = new FakeTrayIconController();
        await using (var disabledHost = CreateHost(
            new FakeProcessInstanceLock(),
            new FakeWindowsIpcServerHost(),
            new FakeWindowsAgentEndpointHost(),
            new FakeWindowsAgentBackendRuntimeOwner(),
            disabledTray,
            backgroundSync: new FakeWindowsBackgroundSyncCoordinator
            {
                State = FakeWindowsBackgroundSyncCoordinator.DisabledState()
            }))
        {
            await disabledHost.StartAsync();
            Assert.AreEqual(false, disabledTray.LastVisible);
        }
    }

    [TestMethod]
    public async Task BackgroundRestorationFailureRollsBackStartupBeforeAdmissionOpens()
    {
        var processLock = new FakeProcessInstanceLock();
        var admissionGate = new WindowsAgentAdmissionGate();
        var control = new FakeWindowsIpcServerHost();
        var endpoint = new FakeWindowsAgentEndpointHost();
        var background = new FakeWindowsBackgroundSyncCoordinator
        {
            InitializeFailure = new IOException("background restoration")
        };
        await using var host = CreateHost(
            processLock,
            control,
            endpoint,
            new FakeWindowsAgentBackendRuntimeOwner(),
            new FakeTrayIconController(),
            admissionGate: admissionGate,
            backgroundSync: background);

        await Assert.ThrowsExactlyAsync<IOException>(() => host.StartAsync());

        Assert.AreEqual(1, background.InitializeCount);
        Assert.AreEqual(0, control.StartCount);
        Assert.AreEqual(0, endpoint.StartCount);
        Assert.AreEqual(AgentAdmissionState.Closed, admissionGate.State);
        Assert.IsTrue(processLock.IsDisposed);
    }

    [TestMethod]
    public async Task TrayExitWaitsForBackgroundTransitionBeforeUiHandshake()
    {
        var lifecycleTransitions = new WindowsAgentLifecycleTransitionCoordinator();
        var close = new FakeWindowsUiCloseService();
        var coordinator = new FakeUiConnectionCoordinator
        {
            Registration = FakeUiConnectionCoordinator.CreateRegistration()
        };
        await using var host = CreateHost(
            new FakeProcessInstanceLock(),
            new FakeWindowsIpcServerHost(),
            new FakeWindowsAgentEndpointHost(),
            new FakeWindowsAgentBackendRuntimeOwner(),
            new FakeTrayIconController(),
            close: close,
            coordinator: coordinator,
            lifecycleTransitions: lifecycleTransitions);
        await host.StartAsync();
        await using var backgroundTransition = await lifecycleTransitions.EnterAsync(
            WindowsAgentLifecycleTransitionState.ChangingBackgroundSync);

        var exit = host.RequestShutdownAsync(WindowsAgentShutdownReason.UserRequestedExit);
        await Task.Delay(25);

        Assert.AreEqual(0, close.RequestCount);
        await backgroundTransition.DisposeAsync();
        var result = await exit;

        Assert.AreEqual(WindowsAgentShutdownResultKind.Completed, result.Kind);
        Assert.AreEqual(1, close.RequestCount);
    }

    [TestMethod]
    public async Task ShutdownReleasesBackgroundLeaseBeforeBackendStopWithoutChangingSetting()
    {
        var operations = new List<string>();
        var background = new FakeWindowsBackgroundSyncCoordinator
        {
            State = FakeWindowsBackgroundSyncCoordinator.OperationalState(),
            OperationLog = operations
        };
        var backend = new FakeWindowsAgentBackendRuntimeOwner { OperationLog = operations };
        await using var host = CreateHost(
            new FakeProcessInstanceLock(),
            new FakeWindowsIpcServerHost(),
            new FakeWindowsAgentEndpointHost(),
            backend,
            new FakeTrayIconController(),
            backgroundSync: background);
        await host.StartAsync();
        operations.Clear();

        var result = await host.RequestShutdownAsync(WindowsAgentShutdownReason.UserRequestedExit);

        Assert.AreEqual(WindowsAgentShutdownResultKind.Completed, result.Kind);
        Assert.AreEqual(1, background.ShutdownCount);
        Assert.IsTrue(operations.IndexOf("background-shutdown") < operations.IndexOf("backend-stop"));
        Assert.IsTrue(background.State.IsEnabled);
        Assert.IsFalse(background.State.IsBackgroundLeaseActive);
    }


    [TestMethod]
    public async Task BackgroundLeaseCleanupFailureRetainsProcessOwnership()
    {
        var processLock = new FakeProcessInstanceLock();
        var background = new FakeWindowsBackgroundSyncCoordinator
        {
            State = FakeWindowsBackgroundSyncCoordinator.OperationalState(),
            ShutdownFailure = new IOException("background lease cleanup")
        };
        var host = CreateHost(
            processLock,
            new FakeWindowsIpcServerHost(),
            new FakeWindowsAgentEndpointHost(),
            new FakeWindowsAgentBackendRuntimeOwner(),
            new FakeTrayIconController(),
            backgroundSync: background);
        await host.StartAsync();

        var result = await host.RequestShutdownAsync(
            WindowsAgentShutdownReason.ApplicationExit);

        Assert.AreEqual(WindowsAgentShutdownResultKind.Failed, result.Kind);
        Assert.IsFalse(processLock.IsDisposed);
        Assert.IsTrue(host.RetainsProcessOwnershipUntilTermination);
        Assert.AreEqual(1, background.ShutdownCount);
        await Assert.ThrowsExactlyAsync<IOException>(() => host.DisposeAsync().AsTask());
    }

    [TestMethod]
    public async Task ShutdownStopsPreferenceReloadBeforeTrayDisposal()
    {
        var operations = new List<string>();
        var tray = new FakeTrayIconController { OperationLog = operations };
        var preferenceReload = new FakeAgentApplicationPreferencesReloadCoordinator
        {
            OperationLog = operations
        };
        var host = CreateHost(
            new FakeProcessInstanceLock(),
            new FakeWindowsIpcServerHost(),
            new FakeWindowsAgentEndpointHost(),
            new FakeWindowsAgentBackendRuntimeOwner(),
            tray,
            preferencesReloadCoordinator: preferenceReload);
        await host.StartAsync();

        var result = await host.RequestShutdownAsync(
            WindowsAgentShutdownReason.ApplicationExit);

        Assert.AreEqual(WindowsAgentShutdownResultKind.Completed, result.Kind);
        Assert.AreEqual(1, preferenceReload.DisposeCount);
        Assert.IsTrue(operations.IndexOf("localization-stop") >= 0);
        Assert.IsTrue(operations.IndexOf("tray-dispose") > operations.IndexOf("localization-stop"));
        await host.DisposeAsync();
        Assert.AreEqual(1, preferenceReload.DisposeCount);
    }

    private static WindowsAgentHost CreateHost(
        FakeProcessInstanceLock processLock,
        FakeWindowsIpcServerHost control,
        FakeWindowsAgentEndpointHost endpoint,
        FakeWindowsAgentBackendRuntimeOwner backend,
        FakeTrayIconController tray,
        FakeWindowsUiOpenService? open = null,
        FakeWindowsUiCloseService? close = null,
        FakeUiConnectionCoordinator? coordinator = null,
        WindowsAgentShutdownCoordinator? shutdownCoordinator = null,
        WindowsAgentStateStore? state = null,
        FakeProcessInstanceLockProbe? uiProcessLockProbe = null,
        WindowsAgentAdmissionGate? admissionGate = null,
        TimeSpan? uiRegistrationPreflightTimeout = null,
        TimeSpan? uiRegistrationPollInterval = null,
        FakeWindowsBackgroundSyncCoordinator? backgroundSync = null,
        WindowsAgentLifecycleTransitionCoordinator? lifecycleTransitions = null,
        IAgentApplicationPreferencesReloadCoordinator? preferencesReloadCoordinator = null) =>
        new(
            processLock,
            uiProcessLockProbe ?? new FakeProcessInstanceLockProbe(),
            admissionGate ?? new WindowsAgentAdmissionGate(),
            control,
            endpoint,
            backend,
            backgroundSync ?? new FakeWindowsBackgroundSyncCoordinator(),
            lifecycleTransitions ?? new WindowsAgentLifecycleTransitionCoordinator(),
            tray,
            open ?? new FakeWindowsUiOpenService(),
            close ?? new FakeWindowsUiCloseService(),
            coordinator ?? new FakeUiConnectionCoordinator(),
            shutdownCoordinator ?? new WindowsAgentShutdownCoordinator(),
            state ?? new WindowsAgentStateStore(),
            AgentLocalizationTestFactory.CreateEnglish(),
            uiRegistrationPreflightTimeout,
            uiRegistrationPollInterval,
            applicationPreferencesReloadCoordinator: preferencesReloadCoordinator);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
