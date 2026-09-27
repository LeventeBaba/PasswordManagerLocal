using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Windows.Agent.Backend;
using PasswordManagerLocal.Windows.Agent.Endpoint;
using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Windows.Agent.Status;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using PasswordManagerLocal.Windows.Ipc.Validation;
using System.Reflection;

namespace PasswordManagerLocal.Windows.Tests.IPC.Agent;

[TestClass]
public sealed class WindowsAgentStatusProviderTests
{
    [TestMethod]
    public async Task StatusReportsAuthoritativeAgentOwnedRuntimeAndEndpointState()
    {
        var state = new WindowsAgentStateStore();
        state.MarkRunning(DateTimeOffset.UtcNow);
        var coordinator = new SingleUiConnectionCoordinator();
        Assert.IsTrue(coordinator.TryRegister(CreateUiContext(), out _));
        var session = new FakeAgentInteractiveBackendSession(
            DispatchProxy.Create<IEndpoints, TestEndpointsDispatchProxy>());
        var lease = new FakeAgentBackendRuntimeLease(BackendLifetimeReason.InteractiveUi);
        var owner = new FakeWindowsAgentBackendRuntimeOwner
        {
            Snapshot = FakeWindowsAgentBackendRuntimeOwner.CreateSnapshot(
                ownerState: PasswordManagerLocal.Windows.Agent.Backend.WindowsAgentBackendOwnerState.Interactive,
                runtimeState: BackendRuntimeState.Ready,
                interactiveState: InteractiveSessionLifecycleState.Active,
                syncState: SyncRuntimeState.Disabled,
                activeReasons: BackendLifetimeReason.InteractiveUi),
            InteractiveBindingFactory = _ => Task.FromResult(
                new AgentInteractiveBackendBinding(session, lease, () => ValueTask.CompletedTask))
        };
        var endpointHost = new FakeWindowsAgentEndpointHost
        {
            Snapshot = new WindowsAgentEndpointHostSnapshot(
                WindowsAgentEndpointHostState.Ready,
                null,
                DateTimeOffset.UtcNow)
        };
        await using var adapter = new AgentInteractiveEndpointAdapter(owner);
        var endpointConnection = CreateEndpointContext();
        await adapter.OnConnectionLifecycleChangedAsync(new IpcConnectionLifecycleNotification(
            endpointConnection.ConnectionId,
            IpcConnectionLifecycleState.HandshakeCompleted,
            endpointConnection,
            IpcDisconnectKind.None,
            DateTimeOffset.UtcNow));
        var reset = new FakeWindowsAgentDatabaseResetCoordinator();
        var provider = new WindowsAgentStatusProvider(
            state,
            CreateOpenAdmissionGate(),
            coordinator,
            new FakeWindowsBackgroundSyncCoordinator
            {
                State = new WindowsBackgroundSyncStateDto(
                    IsEnabled: true,
                    IsStartupRegistered: true,
                    IsBackgroundLeaseActive: false,
                    IsRuntimeRunning: true,
                    IsTransitionInProgress: false,
                    WindowsBackgroundSyncConsistency.Degraded,
                    WindowsBackgroundSyncFailureKind.RuntimeLease,
                    new IpcFailureDto(
                        IpcFailureKind.BackgroundConfiguration,
                        "Background synchronization is not active.",
                        DateTimeOffset.UtcNow,
                        IsRetryable: true,
                        RequiresProcessRestart: false))
            },
            owner,
            endpointHost,
            adapter,
            reset,
            AgentLocalizationTestFactory.CreateEnglish());

        var agent = await provider.GetAgentStatusAsync(CancellationToken.None);
        var backend = await provider.GetBackendRuntimeStatusAsync(CancellationToken.None);
        var interactive = await provider.GetInteractiveSessionStatusAsync(CancellationToken.None);
        var synchronization = await provider.GetSynchronizationStatusAsync(CancellationToken.None);

        Assert.AreEqual(AgentState.Running, agent.AgentState);
        Assert.AreEqual(AgentAdmissionState.Open, agent.AdmissionState);
        Assert.IsTrue(agent.IsUiConnected);
        Assert.IsTrue(agent.BackendOwnedByAgent);
        Assert.IsTrue(agent.IsBackendRunning);
        Assert.IsTrue(agent.IsEndpointHostReady);
        Assert.IsTrue(agent.HasInteractiveUiLease);
        Assert.IsFalse(agent.HasBackgroundSyncLease);
        Assert.IsTrue(agent.IsBackgroundSyncEnabled);
        Assert.AreEqual(BackendRuntimeStatusState.Ready, backend.RuntimeState);
        Assert.AreEqual(InteractiveSessionStatusState.Active, interactive.LifecycleState);
        Assert.IsTrue(interactive.AcceptsNewOperations);
        Assert.AreEqual(SynchronizationStatusState.Disabled, synchronization.State);
        ValidateAll(agent, backend, interactive, synchronization);
    }

    [TestMethod]
    public async Task SettingsReadFailureDoesNotInventBackgroundLeaseOrActivity()
    {
        var state = new WindowsAgentStateStore();
        state.MarkRunning(DateTimeOffset.UtcNow);
        var owner = new FakeWindowsAgentBackendRuntimeOwner();
        var endpointHost = new FakeWindowsAgentEndpointHost
        {
            Snapshot = new WindowsAgentEndpointHostSnapshot(
                WindowsAgentEndpointHostState.Ready,
                null,
                DateTimeOffset.UtcNow)
        };
        await using var adapter = new AgentInteractiveEndpointAdapter(owner);
        var provider = new WindowsAgentStatusProvider(
            state,
            CreateOpenAdmissionGate(),
            new SingleUiConnectionCoordinator(),
            new FakeWindowsBackgroundSyncCoordinator { State = new WindowsBackgroundSyncStateDto(false, false, false, false, false, WindowsBackgroundSyncConsistency.Unavailable, WindowsBackgroundSyncFailureKind.SettingRead, new IpcFailureDto(IpcFailureKind.BackgroundConfiguration, "unavailable", DateTimeOffset.UtcNow, true, false)) },
            owner,
            endpointHost,
            adapter,
            new FakeWindowsAgentDatabaseResetCoordinator(),
            AgentLocalizationTestFactory.CreateEnglish());

        var status = await provider.GetAgentStatusAsync(CancellationToken.None);

        Assert.IsFalse(status.IsBackgroundSyncEnabled);
        Assert.IsFalse(status.HasBackgroundSyncLease);
        Assert.IsFalse(status.IsBackendRunning);
        Assert.IsTrue(status.BackendOwnedByAgent);
        new WindowsIpcContractValidator().Validate(status);
    }

    [TestMethod]
    public async Task RestartRequiredStateIsReportedWithoutClaimingEndpointReadiness()
    {
        var state = new WindowsAgentStateStore();
        state.MarkRunning(DateTimeOffset.UtcNow);
        var failure = new InvalidOperationException("unsafe same-process recovery");
        var owner = new FakeWindowsAgentBackendRuntimeOwner
        {
            Snapshot = FakeWindowsAgentBackendRuntimeOwner.CreateSnapshot(
                ownerState: PasswordManagerLocal.Windows.Agent.Backend.WindowsAgentBackendOwnerState.RestartRequired,
                runtimeState: BackendRuntimeState.Failed,
                runtimeFailureKind: BackendRuntimeFailureKind.ShutdownFailure,
                requiresProcessRestart: true,
                failure: failure)
        };
        var endpointHost = new FakeWindowsAgentEndpointHost
        {
            Snapshot = new WindowsAgentEndpointHostSnapshot(
                WindowsAgentEndpointHostState.Stopped,
                null,
                DateTimeOffset.UtcNow)
        };
        await using var adapter = new AgentInteractiveEndpointAdapter(owner);
        var provider = new WindowsAgentStatusProvider(
            state,
            new WindowsAgentAdmissionGate(),
            new SingleUiConnectionCoordinator(),
            new FakeWindowsBackgroundSyncCoordinator(),
            owner,
            endpointHost,
            adapter,
            new FakeWindowsAgentDatabaseResetCoordinator(),
            AgentLocalizationTestFactory.CreateEnglish());

        var agent = await provider.GetAgentStatusAsync(CancellationToken.None);
        var backend = await provider.GetBackendRuntimeStatusAsync(CancellationToken.None);

        Assert.IsTrue(agent.RequiresProcessRestart);
        Assert.IsFalse(agent.IsEndpointHostReady);
        Assert.IsFalse(agent.IsBackendRunning);
        Assert.IsNotNull(agent.LastFailure);
        Assert.IsTrue(backend.RequiresProcessRestart);
        Assert.AreEqual(BackendRuntimeStatusState.Failed, backend.RuntimeState);
        var validator = new WindowsIpcContractValidator();
        validator.Validate(agent);
        validator.Validate(backend);
    }

    [TestMethod]
    public async Task RuntimeOwnerFailureKeepsListenerStatusSeparateAndReportsStartupFailure()
    {
        var state = new WindowsAgentStateStore();
        state.MarkRunning(DateTimeOffset.UtcNow);
        var owner = new FakeWindowsAgentBackendRuntimeOwner
        {
            Snapshot = FakeWindowsAgentBackendRuntimeOwner.CreateSnapshot(
                ownerState: PasswordManagerLocal.Windows.Agent.Backend.WindowsAgentBackendOwnerState.Failed,
                runtimeState: BackendRuntimeState.NotStarted,
                failure: new IOException("runtime factory failed"))
        };
        var endpointHost = new FakeWindowsAgentEndpointHost
        {
            Snapshot = new WindowsAgentEndpointHostSnapshot(
                WindowsAgentEndpointHostState.Ready,
                null,
                DateTimeOffset.UtcNow)
        };
        await using var adapter = new AgentInteractiveEndpointAdapter(owner);
        var provider = new WindowsAgentStatusProvider(
            state,
            CreateOpenAdmissionGate(),
            new SingleUiConnectionCoordinator(),
            new FakeWindowsBackgroundSyncCoordinator(),
            owner,
            endpointHost,
            adapter,
            new FakeWindowsAgentDatabaseResetCoordinator(),
            AgentLocalizationTestFactory.CreateEnglish());

        var agent = await provider.GetAgentStatusAsync(CancellationToken.None);
        var backend = await provider.GetBackendRuntimeStatusAsync(CancellationToken.None);

        Assert.IsTrue(agent.IsEndpointHostReady);
        Assert.IsNull(agent.LastFailure);
        Assert.AreEqual(BackendRuntimeStatusState.Failed, backend.RuntimeState);
        Assert.AreEqual(BackendRuntimeFailureStatusKind.StartupFailure, backend.FailureKind);
        Assert.IsNotNull(backend.Failure);
        var validator = new WindowsIpcContractValidator();
        validator.Validate(agent);
        validator.Validate(backend);
    }

    [TestMethod]
    public async Task DatabaseCompatibilityStatusCarriesAuthoritativeAgentVersions()
    {
        var state = new WindowsAgentStateStore();
        state.MarkRunning(DateTimeOffset.UtcNow);
        var owner = new FakeWindowsAgentBackendRuntimeOwner
        {
            Snapshot = FakeWindowsAgentBackendRuntimeOwner.CreateSnapshot(
                ownerState: PasswordManagerLocal.Windows.Agent.Backend.WindowsAgentBackendOwnerState.Failed,
                runtimeState: BackendRuntimeState.Failed,
                runtimeFailureKind: BackendRuntimeFailureKind.DatabaseCompatibility,
                failure: new PasswordManagerLocal.Common.Contracts.Errors.DatabaseVersionNotSupportedException(
                    detectedVersion: 99,
                    oldestSupportedVersion: 12,
                    currentVersion: 12))
        };
        var endpointHost = new FakeWindowsAgentEndpointHost();
        await using var adapter = new AgentInteractiveEndpointAdapter(owner);
        var provider = new WindowsAgentStatusProvider(
            state,
            new WindowsAgentAdmissionGate(),
            new SingleUiConnectionCoordinator(),
            new FakeWindowsBackgroundSyncCoordinator(),
            owner,
            endpointHost,
            adapter,
            new FakeWindowsAgentDatabaseResetCoordinator(),
            AgentLocalizationTestFactory.CreateEnglish());

        var status = await provider.GetBackendRuntimeStatusAsync(CancellationToken.None);

        Assert.AreEqual(BackendRuntimeFailureStatusKind.DatabaseCompatibility, status.FailureKind);
        Assert.IsNotNull(status.DatabaseCompatibility);
        Assert.AreEqual(99, status.DatabaseCompatibility.DetectedVersion);
        Assert.AreEqual(12, status.DatabaseCompatibility.OldestSupportedVersion);
        Assert.AreEqual(12, status.DatabaseCompatibility.CurrentVersion);
        new WindowsIpcContractValidator().Validate(status);
    }

    [TestMethod]
    public async Task ShutdownCleanupFailureRequiresReplacementAndCannotReportReady()
    {
        var state = new WindowsAgentStateStore();
        state.MarkRunning(DateTimeOffset.UtcNow);
        state.MarkStopping();
        state.MarkShutdownFailed("cleanup failed", requiresProcessRestart: true);
        var owner = new FakeWindowsAgentBackendRuntimeOwner
        {
            Snapshot = FakeWindowsAgentBackendRuntimeOwner.CreateSnapshot(
                ownerState: PasswordManagerLocal.Windows.Agent.Backend.WindowsAgentBackendOwnerState.Stopped,
                runtimeState: BackendRuntimeState.Stopped)
        };
        var endpointHost = new FakeWindowsAgentEndpointHost
        {
            Snapshot = new WindowsAgentEndpointHostSnapshot(
                WindowsAgentEndpointHostState.Ready,
                null,
                DateTimeOffset.UtcNow)
        };
        await using var adapter = new AgentInteractiveEndpointAdapter(owner);
        var provider = new WindowsAgentStatusProvider(
            state,
            new WindowsAgentAdmissionGate(),
            new SingleUiConnectionCoordinator(),
            new FakeWindowsBackgroundSyncCoordinator(),
            owner,
            endpointHost,
            adapter,
            new FakeWindowsAgentDatabaseResetCoordinator(),
            AgentLocalizationTestFactory.CreateEnglish());

        var status = await provider.GetAgentStatusAsync(CancellationToken.None);

        Assert.AreEqual(AgentState.Failed, status.AgentState);
        Assert.IsTrue(status.RequiresProcessRestart);
        Assert.IsFalse(status.IsEndpointHostReady);
        Assert.IsNotNull(status.LastFailure);
        Assert.IsTrue(status.LastFailure.RequiresProcessRestart);
        new WindowsIpcContractValidator().Validate(status);
    }

    [TestMethod]
    public async Task ResetStateCannotReportEndpointReady()
    {
        var state = new WindowsAgentStateStore();
        state.MarkRunning(DateTimeOffset.UtcNow);
        var owner = new FakeWindowsAgentBackendRuntimeOwner
        {
            Snapshot = FakeWindowsAgentBackendRuntimeOwner.CreateSnapshot(
                ownerState: PasswordManagerLocal.Windows.Agent.Backend.WindowsAgentBackendOwnerState.Resetting,
                isResetting: true)
        };
        var endpointHost = new FakeWindowsAgentEndpointHost();
        var reset = new FakeWindowsAgentDatabaseResetCoordinator { IsResetting = true };
        await using var adapter = new AgentInteractiveEndpointAdapter(owner);
        var provider = new WindowsAgentStatusProvider(
            state,
            CreateOpenAdmissionGate(),
            new SingleUiConnectionCoordinator(),
            new FakeWindowsBackgroundSyncCoordinator(),
            owner,
            endpointHost,
            adapter,
            reset,
            AgentLocalizationTestFactory.CreateEnglish());

        var status = await provider.GetAgentStatusAsync(CancellationToken.None);

        Assert.IsTrue(status.IsDatabaseResetInProgress);
        Assert.IsFalse(status.IsEndpointHostReady);
        new WindowsIpcContractValidator().Validate(status);
    }

    private static WindowsAgentAdmissionGate CreateOpenAdmissionGate()
    {
        var gate = new WindowsAgentAdmissionGate();
        gate.Open();
        return gate;
    }

    private static IpcConnectionContext CreateUiContext() => new(
        Guid.NewGuid(),
        IpcPeerRole.Ui,
        PeerProcessId: 100,
        PeerWindowsSessionId: 2,
        PeerSessionId: Guid.NewGuid(),
        PeerCapabilities: IpcCapabilities.Control | IpcCapabilities.Status);

    private static IpcConnectionContext CreateEndpointContext() => new(
        Guid.NewGuid(),
        IpcPeerRole.Ui,
        PeerProcessId: 100,
        PeerWindowsSessionId: 2,
        PeerSessionId: Guid.NewGuid(),
        PeerCapabilities: IpcCapabilities.EndpointRpc);

    private static void ValidateAll(
        AgentStatusDto agent,
        BackendRuntimeStatusDto backend,
        InteractiveSessionStatusDto interactive,
        SynchronizationStatusDto synchronization)
    {
        var validator = new WindowsIpcContractValidator();
        validator.Validate(agent);
        validator.Validate(backend);
        validator.Validate(interactive);
        validator.Validate(synchronization);
    }
}
