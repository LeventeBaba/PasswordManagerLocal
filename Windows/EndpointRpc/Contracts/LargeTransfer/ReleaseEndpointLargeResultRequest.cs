namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;

public sealed class ReleaseEndpointLargeResultRequest
{
    public Guid TransferId { get; set; }
    public long OriginalCorrelationId { get; set; }
}
