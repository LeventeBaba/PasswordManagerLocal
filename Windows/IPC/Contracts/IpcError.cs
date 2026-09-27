namespace PasswordManagerLocal.Windows.Ipc.Contracts;

public sealed record IpcError(
    IpcErrorCode ErrorCode,
    IpcErrorCategory ErrorCategory,
    string SafeMessage,
    long CorrelationId,
    DateTimeOffset OccurredAtUtc,
    bool IsRetryable,
    bool RequiresProcessRestart);
