namespace PasswordManagerLocal.Windows.Ipc.Contracts.Errors;

public sealed record IpcError(
    IpcErrorCode ErrorCode,
    IpcErrorCategory ErrorCategory,
    string SafeMessage,
    DateTimeOffset OccurredAtUtc,
    bool IsRetryable,
    bool RequiresProcessRestart);
