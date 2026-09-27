using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public sealed record IpcHandshakeAuthorizationDecision(
    bool IsAuthorized,
    IpcErrorCode ErrorCode,
    string SafeMessage,
    bool IsRetryable)
{
    public static IpcHandshakeAuthorizationDecision Authorized { get; } =
        new(true, IpcErrorCode.RequestRejected, string.Empty, false);

    public static IpcHandshakeAuthorizationDecision Reject(
        IpcErrorCode errorCode,
        string safeMessage,
        bool isRetryable = false)
    {
        if (string.IsNullOrWhiteSpace(safeMessage))
            throw new ArgumentException("The IPC authorization message cannot be empty.", nameof(safeMessage));

        return new(false, errorCode, safeMessage, isRetryable);
    }
}
