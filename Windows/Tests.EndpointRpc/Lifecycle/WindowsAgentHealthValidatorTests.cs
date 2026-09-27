using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Lifecycle;

[TestClass]
public sealed class WindowsAgentHealthValidatorTests
{
    private readonly WindowsAgentHealthValidator _validator = new();

    [TestMethod]
    public void RunningOpenAgentWithUsableBackendIsEndpointHealthy()
    {
        var agent = CreateAgentStatus();
        var backend = CreateBackendStatus(BackendRuntimeStatusState.Ready);

        Assert.IsTrue(_validator.CanRegisterUi(agent, backend));
        Assert.IsTrue(_validator.IsHealthyForEndpoint(agent, backend));
        Assert.IsFalse(_validator.RequiresProcessReplacement(agent, backend));
    }

    [TestMethod]
    public void StoppedRestartableBackendIsEndpointHealthy()
    {
        var agent = CreateAgentStatus();
        var backend = CreateBackendStatus(BackendRuntimeStatusState.Stopped);

        Assert.IsTrue(_validator.CanRegisterUi(agent, backend));
        Assert.IsTrue(_validator.IsHealthyForEndpoint(agent, backend));
        Assert.IsFalse(_validator.RequiresProcessReplacement(agent, backend));
    }

    [TestMethod]
    public void FailedStoppingAndClosedAdmissionAgentsRequireReplacement()
    {
        foreach (var agent in new[]
        {
            CreateAgentStatus(agentState: AgentState.Failed, admissionState: AgentAdmissionState.Closed),
            CreateAgentStatus(agentState: AgentState.Stopping, admissionState: AgentAdmissionState.Closed),
            CreateAgentStatus(admissionState: AgentAdmissionState.Closed)
        })
        {
            var backend = CreateBackendStatus(BackendRuntimeStatusState.Ready);
            Assert.IsFalse(_validator.CanRegisterUi(agent, backend));
            Assert.IsFalse(_validator.IsHealthyForEndpoint(agent, backend));
            Assert.IsTrue(_validator.RequiresProcessReplacement(agent, backend));
        }
    }

    [TestMethod]
    public void RestartRequiredAgentCannotRegisterOrBecomeEndpointReady()
    {
        var agent = CreateAgentStatus(requiresProcessRestart: true);
        var backend = CreateBackendStatus(
            BackendRuntimeStatusState.Failed,
            BackendRuntimeFailureStatusKind.ShutdownFailure,
            requiresProcessRestart: true);

        Assert.IsFalse(_validator.CanRegisterUi(agent, backend));
        Assert.IsFalse(_validator.IsHealthyForEndpoint(agent, backend));
        Assert.IsTrue(_validator.RequiresProcessReplacement(agent, backend));
    }

    [TestMethod]
    public void ResettableCompatibilityFailureAllowsControlRegistrationButNotEndpointReadiness()
    {
        var agent = CreateAgentStatus();
        var backend = CreateBackendStatus(
            BackendRuntimeStatusState.Failed,
            BackendRuntimeFailureStatusKind.DatabaseCompatibility);

        Assert.IsTrue(_validator.CanRegisterUi(agent, backend));
        Assert.IsFalse(_validator.IsHealthyForEndpoint(agent, backend));
        Assert.IsFalse(_validator.RequiresProcessReplacement(agent, backend));
    }

    [TestMethod]
    public void NonResettableBackendFailureRejectsRegistrationAndRequiresReplacement()
    {
        var agent = CreateAgentStatus();
        var backend = CreateBackendStatus(
            BackendRuntimeStatusState.Failed,
            BackendRuntimeFailureStatusKind.StorageUnavailable);

        Assert.IsFalse(_validator.CanRegisterUi(agent, backend));
        Assert.IsFalse(_validator.IsHealthyForEndpoint(agent, backend));
        Assert.IsTrue(_validator.RequiresProcessReplacement(agent, backend));
    }

    private static AgentStatusDto CreateAgentStatus(
        AgentState agentState = AgentState.Running,
        AgentAdmissionState admissionState = AgentAdmissionState.Open,
        bool requiresProcessRestart = false) =>
        new(
            agentState,
            admissionState,
            IsUiConnected: false,
            BackendOwnedByAgent: true,
            IsBackendRunning: true,
            IsBackgroundSyncEnabled: false,
            RequiresProcessRestart: requiresProcessRestart,
            LastFailure: null,
            StartedAtUtc: DateTimeOffset.UtcNow,
            IsEndpointHostReady: true);

    private static BackendRuntimeStatusDto CreateBackendStatus(
        BackendRuntimeStatusState state,
        BackendRuntimeFailureStatusKind failureKind = BackendRuntimeFailureStatusKind.None,
        bool requiresProcessRestart = false) =>
        new(
            state,
            failureKind,
            Failure: null,
            RequiresProcessRestart: requiresProcessRestart,
            ChangedAtUtc: DateTimeOffset.UtcNow,
            DatabaseCompatibility: failureKind == BackendRuntimeFailureStatusKind.DatabaseCompatibility
                ? new DatabaseCompatibilityStatusDto(99, 12, 12)
                : null);
}
