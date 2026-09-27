namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public sealed record AgentStatusDto(
    AgentState AgentState,
    AgentAdmissionState AdmissionState,
    bool IsUiConnected,
    bool BackendOwnedByAgent,
    bool IsBackendRunning,
    bool IsBackgroundSyncEnabled,
    bool RequiresProcessRestart,
    IpcFailureDto? LastFailure,
    DateTimeOffset? StartedAtUtc,
    bool IsEndpointHostReady = false,
    bool IsDatabaseResetInProgress = false,
    bool HasInteractiveUiLease = false,
    bool HasBackgroundSyncLease = false);
