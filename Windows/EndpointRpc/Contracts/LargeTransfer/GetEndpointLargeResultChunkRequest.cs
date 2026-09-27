namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;

public sealed class GetEndpointLargeResultChunkRequest
{
    public Guid TransferId { get; set; }
    public long OriginalCorrelationId { get; set; }
    public int ChunkIndex { get; set; }
}
