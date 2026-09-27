namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

public sealed class EndpointRpcDisconnectedException : InvalidOperationException
{
    public EndpointRpcDisconnectedException()
        : base("The endpoint RPC connection is not available.")
    {
    }

    public EndpointRpcDisconnectedException(Exception innerException)
        : base("The endpoint RPC connection is not available.", innerException)
    {
    }
}
