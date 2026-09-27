namespace PasswordManagerLocal.Windows.Ipc.Transport;

public sealed class IpcConnectionClosedException : IOException
{
    public IpcConnectionClosedException(string message)
        : base(message)
    {
    }

    public IpcConnectionClosedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
