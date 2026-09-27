namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;

public sealed class GetEndpointLargeResultChunkResponse
{
    public Guid TransferId { get; set; }
    public long OriginalCorrelationId { get; set; }
    public int ChunkIndex { get; set; }
    public bool IsFinal { get; set; }
    public int DeclaredTotalLength { get; set; }
    public int DeclaredChunkCount { get; set; }
    public byte[] ChunkPayload { get; set; } = [];
}
