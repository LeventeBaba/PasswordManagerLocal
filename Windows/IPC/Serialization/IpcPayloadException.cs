namespace PasswordManagerLocal.Windows.Ipc.Serialization;

public class IpcPayloadException : Exception
{
    public IpcPayloadException(string message)
        : base(message)
    {
    }

    public IpcPayloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
