using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Ipc.Client;

public sealed class IpcRemoteException : Exception
{
    public IpcRemoteException(IpcError error)
        : base(error?.SafeMessage)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public IpcError Error { get; }
}
