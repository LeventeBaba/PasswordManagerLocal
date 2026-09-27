namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public sealed record InteractiveSessionStatusDto(
    InteractiveSessionStatusState LifecycleState,
    bool AcceptsNewOperations,
    int ActiveOperationCount,
    IpcFailureDto? CleanupFailure,
    DateTimeOffset ChangedAtUtc);
