using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Ipc.Client;

public sealed class IpcRemoteException : Exception
{
    public IpcRemoteException(IpcError error, long correlationId = 0)
        : base(error?.SafeMessage)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
        CorrelationId = correlationId;
    }

    public IpcError Error { get; }
    public long CorrelationId { get; }
}
