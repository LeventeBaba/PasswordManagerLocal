using PasswordManagerLocal.Windows.EndpointRpc.Contracts;

namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

public sealed class EndpointRpcRemoteException : Exception
{
    public EndpointRpcRemoteException(EndpointRpcError error)
        : base(error?.SafeMessage)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public EndpointRpcError Error { get; }
}
