using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

public sealed class WindowsAgentHealthValidator
{
    public bool CanRegisterUi(
        AgentStatusDto agentStatus,
        BackendRuntimeStatusDto backendStatus)
    {
        ValidateArguments(agentStatus, backendStatus);
        return IsAgentShellOperational(agentStatus) &&
            !backendStatus.RequiresProcessRestart &&
            (backendStatus.RuntimeState != BackendRuntimeStatusState.Failed
                ? backendStatus.RuntimeState is not BackendRuntimeStatusState.Stopping and
                    not BackendRuntimeStatusState.Unavailable
                : backendStatus.FailureKind == BackendRuntimeFailureStatusKind.DatabaseCompatibility);
    }

    public bool IsHealthyForEndpoint(
        AgentStatusDto agentStatus,
        BackendRuntimeStatusDto backendStatus)
    {
        ValidateArguments(agentStatus, backendStatus);
        return IsAgentShellOperational(agentStatus) &&
            !backendStatus.RequiresProcessRestart &&
            backendStatus.RuntimeState is
                BackendRuntimeStatusState.NotStarted or
                BackendRuntimeStatusState.Starting or
                BackendRuntimeStatusState.Ready or
                BackendRuntimeStatusState.Stopped or
                BackendRuntimeStatusState.WaitingForDeviceUnlock;
    }

    public bool RequiresProcessReplacement(
        AgentStatusDto agentStatus,
        BackendRuntimeStatusDto backendStatus)
    {
        ValidateArguments(agentStatus, backendStatus);
        return agentStatus.AgentState is AgentState.Stopping or AgentState.Stopped or AgentState.Failed ||
            agentStatus.AdmissionState != AgentAdmissionState.Open ||
            agentStatus.RequiresProcessRestart ||
            backendStatus.RequiresProcessRestart ||
            (backendStatus.RuntimeState == BackendRuntimeStatusState.Failed &&
                backendStatus.FailureKind != BackendRuntimeFailureStatusKind.DatabaseCompatibility);
    }

    private static bool IsAgentShellOperational(AgentStatusDto agentStatus) =>
        agentStatus.AgentState == AgentState.Running &&
        agentStatus.AdmissionState == AgentAdmissionState.Open &&
        agentStatus.BackendOwnedByAgent &&
        agentStatus.IsEndpointHostReady &&
        !agentStatus.IsDatabaseResetInProgress &&
        !agentStatus.RequiresProcessRestart;

    private static void ValidateArguments(
        AgentStatusDto agentStatus,
        BackendRuntimeStatusDto backendStatus)
    {
        ArgumentNullException.ThrowIfNull(agentStatus);
        ArgumentNullException.ThrowIfNull(backendStatus);
    }
}
