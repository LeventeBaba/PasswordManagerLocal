namespace PasswordManagerLocal.Windows.Ipc.Contracts.Errors;

public sealed record IpcFailureDto(
    IpcFailureKind FailureKind,
    string SafeMessage,
    DateTimeOffset OccurredAtUtc,
    bool IsRetryable,
    bool RequiresProcessRestart);
