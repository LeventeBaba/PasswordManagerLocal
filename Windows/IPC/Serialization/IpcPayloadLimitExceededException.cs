using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Ipc.Serialization;

public sealed class IpcPayloadLimitExceededException : IpcPayloadException
{
    public IpcPayloadLimitExceededException(
        IpcErrorCode errorCode,
        string safeMessage,
        long correlationId)
        : base(safeMessage)
    {
        if (errorCode is not IpcErrorCode.RequestPayloadTooLarge and
            not IpcErrorCode.ResponsePayloadTooLarge and
            not IpcErrorCode.SerializedEnvelopeTooLarge)
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode));
        }

        if (string.IsNullOrWhiteSpace(safeMessage))
            throw new ArgumentException("The IPC safe message cannot be empty.", nameof(safeMessage));
        if (correlationId <= 0)
            throw new ArgumentOutOfRangeException(nameof(correlationId));

        ErrorCode = errorCode;
        SafeMessage = safeMessage;
        CorrelationId = correlationId;
    }

    public IpcErrorCode ErrorCode { get; }
    public IpcErrorCategory ErrorCategory => IpcErrorCategory.Validation;
    public string SafeMessage { get; }
    public long CorrelationId { get; }
    public bool IsRetryable => false;
    public bool RequiresProcessRestart => false;
}
