namespace PasswordManagerLocal.Windows.EndpointRpc.Server.LargeTransfer;

internal sealed class EndpointLargeResultCapacityException : Exception
{
    public EndpointLargeResultCapacityException()
        : base("The endpoint large-result transfer capacity is unavailable.")
    {
    }
}
