namespace PasswordManagerLocal.Windows.EndpointRpc.Serialization;

public sealed class EndpointRpcPayloadException : Exception
{
    public EndpointRpcPayloadException(string message) : base(message) { }
    public EndpointRpcPayloadException(string message, Exception innerException) : base(message, innerException) { }
}
