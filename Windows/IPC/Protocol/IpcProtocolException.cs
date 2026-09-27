namespace PasswordManagerLocal.Windows.Ipc.Protocol;

public sealed class IpcProtocolException : Exception
{
    public IpcProtocolException(IpcProtocolErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public IpcProtocolException(
        IpcProtocolErrorCode errorCode,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public IpcProtocolErrorCode ErrorCode { get; }
}
