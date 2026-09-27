namespace PasswordManagerLocal.Common.Backend.Sync.Tcp;

public sealed class SyncProtocolException : Exception
{
    public SyncProtocolException(SyncProtocolStatusCode statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public SyncProtocolStatusCode StatusCode { get; }
}
