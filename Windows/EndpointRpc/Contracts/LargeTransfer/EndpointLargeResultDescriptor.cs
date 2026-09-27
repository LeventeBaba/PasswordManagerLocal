namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;

public sealed class EndpointLargeResultDescriptor
{
    public Guid TransferId { get; set; }
    public long OriginalCorrelationId { get; set; }
    public int DeclaredTotalLength { get; set; }
    public int DeclaredChunkCount { get; set; }
    public int ChunkSize { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
