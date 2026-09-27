namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public sealed record IpcFailureDto(
    IpcFailureKind FailureKind,
    string SafeMessage,
    DateTimeOffset OccurredAtUtc,
    bool IsRetryable,
    bool RequiresProcessRestart);
