namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public sealed record BackendRuntimeStatusDto(
    BackendRuntimeStatusState RuntimeState,
    BackendRuntimeFailureStatusKind FailureKind,
    IpcFailureDto? Failure,
    bool RequiresProcessRestart,
    DateTimeOffset ChangedAtUtc,
    DatabaseCompatibilityStatusDto? DatabaseCompatibility = null);
