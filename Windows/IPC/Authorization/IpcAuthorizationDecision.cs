using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Ipc.Authorization;

public sealed record IpcAuthorizationDecision(
    bool IsAuthorized,
    IpcErrorCode ErrorCode,
    IpcErrorCategory ErrorCategory,
    string SafeMessage,
    bool IsRetryable)
{
    public static IpcAuthorizationDecision Allowed { get; } = new(
        true,
        IpcErrorCode.UnauthorizedOperation,
        IpcErrorCategory.Validation,
        string.Empty,
        false);

    public static IpcAuthorizationDecision Denied(
        IpcErrorCode errorCode,
        IpcErrorCategory errorCategory,
        string safeMessage,
        bool isRetryable = false) =>
        new(false, errorCode, errorCategory, safeMessage, isRetryable);
}
