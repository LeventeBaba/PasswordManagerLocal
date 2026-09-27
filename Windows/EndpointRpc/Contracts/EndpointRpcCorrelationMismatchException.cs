namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts;

public sealed class EndpointRpcCorrelationMismatchException : Exception
{
    public EndpointRpcCorrelationMismatchException()
        : base("The endpoint response correlation does not match the request.")
    {
    }

    public EndpointRpcErrorCode ErrorCode => EndpointRpcErrorCode.EndpointCorrelationMismatch;
    public EndpointRpcErrorCategory ErrorCategory => EndpointRpcErrorCategory.Internal;
    public bool IsRetryable => false;
    public bool RequiresProcessRestart => false;
}
