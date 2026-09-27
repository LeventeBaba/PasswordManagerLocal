namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public sealed record WindowsBackgroundSyncStateDto(
    bool IsEnabled,
    bool IsStartupRegistered,
    bool IsBackgroundLeaseActive,
    bool IsRuntimeRunning,
    bool IsTransitionInProgress,
    WindowsBackgroundSyncConsistency Consistency,
    WindowsBackgroundSyncFailureKind FailureKind,
    IpcFailureDto? Failure);
