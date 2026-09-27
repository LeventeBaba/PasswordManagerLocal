using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Agent.Backend;
using PasswordManagerLocal.Windows.Agent.Hosting;
using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Windows.Ipc.Authorization;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Authorization;

[TestClass]
public sealed class WindowsIpcOperationAuthorizerTests
{
    [TestMethod]
    public void PublicShellOperationsAreAllowedAfterHandshake()
    {
        var authorizer = new WindowsIpcOperationAuthorizer(new SingleUiConnectionCoordinator());
        var connectionId = Guid.NewGuid();

        foreach (var operation in new[]
        {
            IpcOperationId.Ping,
            IpcOperationId.GetAgentStatus,
            IpcOperationId.GetBackendRuntimeStatus,
            IpcOperationId.GetInteractiveSessionStatus,
            IpcOperationId.GetSynchronizationStatus,
            IpcOperationId.GetBackgroundSyncState,
            IpcOperationId.RequestUiOpen,
            IpcOperationId.RequestUiActivation
        })
        {
            Assert.IsTrue(authorizer.Authorize(CreateContext(connectionId, IpcPeerRole.TestClient, operation)).IsAuthorized);
        }
    }

    [TestMethod]
    public void UiRegistrationRequiresUiRole()
    {
        var authorizer = new WindowsIpcOperationAuthorizer(new SingleUiConnectionCoordinator());

        var denied = authorizer.Authorize(CreateContext(
            Guid.NewGuid(),
            IpcPeerRole.TestClient,
            IpcOperationId.RegisterUiConnection));
        var allowed = authorizer.Authorize(CreateContext(
            Guid.NewGuid(),
            IpcPeerRole.Ui,
            IpcOperationId.RegisterUiConnection));

        Assert.IsFalse(denied.IsAuthorized);
        Assert.AreEqual(IpcErrorCode.UnauthorizedOperation, denied.ErrorCode);
        Assert.IsTrue(allowed.IsAuthorized);
    }

    [TestMethod]
    public void UnregisterAndExitRequireCurrentlyRegisteredUiConnection()
    {
        var coordinator = new SingleUiConnectionCoordinator();
        var registered = CreateContext(Guid.NewGuid(), IpcPeerRole.Ui, IpcOperationId.RegisterUiConnection);
        var other = Guid.NewGuid();
        Assert.IsTrue(coordinator.TryRegister(registered.Connection, out _));
        var authorizer = new WindowsIpcOperationAuthorizer(coordinator);

        foreach (var operation in new[]
        {
            IpcOperationId.UnregisterUiConnection,
            IpcOperationId.RequestAgentExit,
            IpcOperationId.ResetDatabase,
            IpcOperationId.SetBackgroundSyncEnabled,
            IpcOperationId.ReloadApplicationPreferences
        })
        {
            Assert.IsTrue(authorizer.Authorize(CreateContext(registered.Connection.ConnectionId, IpcPeerRole.Ui, operation)).IsAuthorized);
            var denied = authorizer.Authorize(CreateContext(other, IpcPeerRole.Ui, operation));
            Assert.IsFalse(denied.IsAuthorized);
            Assert.AreEqual(IpcErrorCode.UiNotRegistered, denied.ErrorCode);
        }
    }

    [TestMethod]
    public void StoppingAgentAllowsOnlyStatusOperations()
    {
        var state = new WindowsAgentStateStore();
        state.MarkRunning(DateTimeOffset.UtcNow);
        var gate = new WindowsAgentAdmissionGate();
        gate.Open();
        gate.ClosePermanently();
        state.MarkStopping();
        var authorizer = new WindowsAgentOperationAuthorizer(
            state,
            gate,
            new FakeWindowsAgentBackendRuntimeOwner(),
            new WindowsIpcOperationAuthorizer(new SingleUiConnectionCoordinator()),
            AgentLocalizationTestFactory.CreateEnglish());

        foreach (var operation in StatusOperations)
        {
            Assert.IsTrue(authorizer.Authorize(
                CreateContext(Guid.NewGuid(), IpcPeerRole.TestClient, operation)).IsAuthorized);
        }

        foreach (var operation in new[]
        {
            IpcOperationId.RegisterUiConnection,
            IpcOperationId.SetBackgroundSyncEnabled
        })
        {
            var denied = authorizer.Authorize(CreateContext(
                Guid.NewGuid(),
                IpcPeerRole.Ui,
                operation));
            Assert.IsFalse(denied.IsAuthorized);
            Assert.AreEqual(IpcErrorCode.AgentStopping, denied.ErrorCode);
        }
    }

    [TestMethod]
    public void FailedAgentAllowsStatusButRejectsRegistrationAndMutations()
    {
        var state = new WindowsAgentStateStore();
        state.MarkFailed("fatal lifecycle failure");
        var authorizer = new WindowsAgentOperationAuthorizer(
            state,
            new WindowsAgentAdmissionGate(),
            new FakeWindowsAgentBackendRuntimeOwner(),
            new WindowsIpcOperationAuthorizer(new SingleUiConnectionCoordinator()),
            AgentLocalizationTestFactory.CreateEnglish());

        foreach (var operation in StatusOperations)
        {
            Assert.IsTrue(authorizer.Authorize(
                CreateContext(Guid.NewGuid(), IpcPeerRole.TestClient, operation)).IsAuthorized);
        }
        foreach (var operation in new[]
        {
            IpcOperationId.RegisterUiConnection,
            IpcOperationId.ResetDatabase,
            IpcOperationId.RequestAgentExit,
            IpcOperationId.SetBackgroundSyncEnabled,
            IpcOperationId.RequestUiOpen
        })
        {
            var denied = authorizer.Authorize(CreateContext(
                Guid.NewGuid(),
                IpcPeerRole.Ui,
                operation));
            Assert.IsFalse(denied.IsAuthorized);
            Assert.AreEqual(IpcErrorCode.AgentUnavailable, denied.ErrorCode);
        }
    }

    [TestMethod]
    public void RestartRequiredBackendMakesRunningAgentStatusOnly()
    {
        var state = new WindowsAgentStateStore();
        state.MarkRunning(DateTimeOffset.UtcNow);
        var gate = new WindowsAgentAdmissionGate();
        gate.Open();
        var backend = new FakeWindowsAgentBackendRuntimeOwner();
        backend.RequireProcessRestart(new IOException("cleanup failed"));
        var authorizer = new WindowsAgentOperationAuthorizer(
            state,
            gate,
            backend,
            new WindowsIpcOperationAuthorizer(new SingleUiConnectionCoordinator()),
            AgentLocalizationTestFactory.CreateEnglish());

        Assert.IsTrue(authorizer.Authorize(CreateContext(
            Guid.NewGuid(),
            IpcPeerRole.TestClient,
            IpcOperationId.GetAgentStatus)).IsAuthorized);
        Assert.IsFalse(authorizer.Authorize(CreateContext(
            Guid.NewGuid(),
            IpcPeerRole.Ui,
            IpcOperationId.RegisterUiConnection)).IsAuthorized);
        Assert.IsFalse(authorizer.Authorize(CreateContext(
            Guid.NewGuid(),
            IpcPeerRole.Ui,
            IpcOperationId.SetBackgroundSyncEnabled)).IsAuthorized);
    }

    [TestMethod]
    public void RunningAgentWithNonResettableBackendFailureIsStatusOnly()
    {
        var state = new WindowsAgentStateStore();
        state.MarkRunning(DateTimeOffset.UtcNow);
        var gate = new WindowsAgentAdmissionGate();
        gate.Open();
        var backend = new FakeWindowsAgentBackendRuntimeOwner
        {
            Snapshot = FakeWindowsAgentBackendRuntimeOwner.CreateSnapshot(
                ownerState: WindowsAgentBackendOwnerState.Failed,
                runtimeState: BackendRuntimeState.Failed,
                runtimeFailureKind: BackendRuntimeFailureKind.StorageUnavailable)
        };
        var authorizer = new WindowsAgentOperationAuthorizer(
            state,
            gate,
            backend,
            new WindowsIpcOperationAuthorizer(new SingleUiConnectionCoordinator()),
            AgentLocalizationTestFactory.CreateEnglish());

        Assert.IsTrue(authorizer.Authorize(CreateContext(
            Guid.NewGuid(),
            IpcPeerRole.TestClient,
            IpcOperationId.GetBackendRuntimeStatus)).IsAuthorized);
        Assert.IsFalse(authorizer.Authorize(CreateContext(
            Guid.NewGuid(),
            IpcPeerRole.Ui,
            IpcOperationId.RegisterUiConnection)).IsAuthorized);
    }

    [TestMethod]
    public void RunningAgentWithResettableBackendFailureAllowsOnlyControlRecoveryOperations()
    {
        var state = new WindowsAgentStateStore();
        state.MarkRunning(DateTimeOffset.UtcNow);
        var gate = new WindowsAgentAdmissionGate();
        gate.Open();
        var coordinator = new SingleUiConnectionCoordinator();
        var backend = new FakeWindowsAgentBackendRuntimeOwner
        {
            Snapshot = FakeWindowsAgentBackendRuntimeOwner.CreateSnapshot(
                ownerState: WindowsAgentBackendOwnerState.Failed,
                runtimeState: BackendRuntimeState.Failed,
                runtimeFailureKind: BackendRuntimeFailureKind.DatabaseCompatibility)
        };
        var authorizer = new WindowsAgentOperationAuthorizer(
            state,
            gate,
            backend,
            new WindowsIpcOperationAuthorizer(coordinator),
            AgentLocalizationTestFactory.CreateEnglish());
        var registration = CreateContext(
            Guid.NewGuid(),
            IpcPeerRole.Ui,
            IpcOperationId.RegisterUiConnection);

        Assert.IsTrue(authorizer.Authorize(registration).IsAuthorized);
        Assert.IsTrue(coordinator.TryRegister(registration.Connection, out _));
        Assert.IsTrue(authorizer.Authorize(CreateContext(
            registration.Connection.ConnectionId,
            IpcPeerRole.Ui,
            IpcOperationId.ResetDatabase)).IsAuthorized);
        Assert.IsTrue(authorizer.Authorize(CreateContext(
            registration.Connection.ConnectionId,
            IpcPeerRole.Ui,
            IpcOperationId.ReloadApplicationPreferences)).IsAuthorized);
        Assert.IsFalse(authorizer.Authorize(CreateContext(
            registration.Connection.ConnectionId,
            IpcPeerRole.Ui,
            IpcOperationId.RequestAgentExit)).IsAuthorized);
    }


    [TestMethod]
    public void AgentBoundaryReplacesSharedAuthorizationMessagesWithLocalizedText()
    {
        var state = new WindowsAgentStateStore();
        state.MarkRunning(DateTimeOffset.UtcNow);
        var gate = new WindowsAgentAdmissionGate();
        gate.Open();
        var authorizer = new WindowsAgentOperationAuthorizer(
            state,
            gate,
            new FakeWindowsAgentBackendRuntimeOwner(),
            new WindowsIpcOperationAuthorizer(new SingleUiConnectionCoordinator()),
            AgentLocalizationTestFactory.CreateEnglish());

        var registrationDenied = authorizer.Authorize(CreateContext(
            Guid.NewGuid(),
            IpcPeerRole.TestClient,
            IpcOperationId.RegisterUiConnection));
        var registeredUiDenied = authorizer.Authorize(CreateContext(
            Guid.NewGuid(),
            IpcPeerRole.Ui,
            IpcOperationId.ReloadApplicationPreferences));
        var unknownDenied = authorizer.Authorize(CreateContext(
            Guid.NewGuid(),
            IpcPeerRole.TestClient,
            (IpcOperationId)ushort.MaxValue));

        Assert.AreEqual(
            AgentLocalizationTestFactory.CreateEnglish().GetString(
                PasswordManagerLocal.Windows.Agent.Localization.AgentLocalizationKeys.IpcUiRegistrationRequired),
            registrationDenied.SafeMessage);
        Assert.AreEqual(
            AgentLocalizationTestFactory.CreateEnglish().GetString(
                PasswordManagerLocal.Windows.Agent.Localization.AgentLocalizationKeys.IpcRegisteredUiRequired),
            registeredUiDenied.SafeMessage);
        Assert.AreEqual(
            AgentLocalizationTestFactory.CreateEnglish().GetString(
                PasswordManagerLocal.Windows.Agent.Localization.AgentLocalizationKeys.IpcUnauthorizedOperation),
            unknownDenied.SafeMessage);
    }

    private static readonly IpcOperationId[] StatusOperations =
    [
        IpcOperationId.Ping,
        IpcOperationId.GetAgentStatus,
        IpcOperationId.GetBackendRuntimeStatus,
        IpcOperationId.GetInteractiveSessionStatus,
        IpcOperationId.GetSynchronizationStatus,
        IpcOperationId.GetBackgroundSyncState
    ];

    private static IpcRequestContext CreateContext(
        Guid connectionId,
        IpcPeerRole role,
        IpcOperationId operation) =>
        new(
            new IpcConnectionContext(
                connectionId,
                role,
                PeerProcessId: 100,
                0,
                PeerSessionId: Guid.NewGuid(),
                PeerCapabilities: IpcCapabilities.Control | IpcCapabilities.Status | IpcCapabilities.UiActivation),
            new IpcRequestEnvelope(1, operation, Payload: null),
            new WindowsIpcSerializer());
}
