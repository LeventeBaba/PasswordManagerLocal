using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;
using PasswordManagerLocal.Windows.EndpointRpc.Security;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;

namespace PasswordManagerLocal.Windows.EndpointRpc.Server.LargeTransfer;

internal sealed class EndpointLargeResultTransfer : IDisposable
{
    private readonly Action _releaseCapacity;
    private byte[]? _payload;
    private int _nextChunkIndex;
    private int _disposed;

    public EndpointLargeResultTransfer(
        Guid ownerConnectionId,
        Guid ownerSessionId,
        long originalCorrelationId,
        byte[] payload,
        DateTimeOffset expiresAtUtc,
        Action releaseCapacity)
    {
        if (ownerConnectionId == Guid.Empty)
            throw new ArgumentException("The owner connection ID cannot be empty.", nameof(ownerConnectionId));
        if (ownerSessionId == Guid.Empty)
            throw new ArgumentException("The owner session ID cannot be empty.", nameof(ownerSessionId));
        if (originalCorrelationId <= 0)
            throw new ArgumentOutOfRangeException(nameof(originalCorrelationId));
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length <= 0 || payload.Length > EndpointRpcLimits.MaximumLargeResultTotalBytes)
            throw new ArgumentOutOfRangeException(nameof(payload));

        var chunkCount = checked((payload.Length + EndpointRpcLimits.MaximumLargeResultChunkBytes - 1) /
            EndpointRpcLimits.MaximumLargeResultChunkBytes);
        if (chunkCount <= 0 || chunkCount > EndpointRpcLimits.MaximumLargeResultChunkCount)
            throw new ArgumentOutOfRangeException(nameof(payload));
        if (expiresAtUtc == default)
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));

        _releaseCapacity = releaseCapacity ?? throw new ArgumentNullException(nameof(releaseCapacity));
        OwnerConnectionId = ownerConnectionId;
        OwnerSessionId = ownerSessionId;
        OriginalCorrelationId = originalCorrelationId;
        TransferId = Guid.NewGuid();
        _payload = payload;
        ExpiresAtUtc = expiresAtUtc;
        ChunkCount = chunkCount;
    }

    public Guid TransferId { get; }
    public Guid OwnerConnectionId { get; }
    public Guid OwnerSessionId { get; }
    public long OriginalCorrelationId { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public int ChunkCount { get; }
    public int TotalLength => _payload?.Length ?? 0;

    public EndpointLargeResultDescriptor CreateDescriptor() =>
        new()
        {
            TransferId = TransferId,
            OriginalCorrelationId = OriginalCorrelationId,
            DeclaredTotalLength = TotalLength,
            DeclaredChunkCount = ChunkCount,
            ChunkSize = EndpointRpcLimits.MaximumLargeResultChunkBytes,
            ExpiresAtUtc = ExpiresAtUtc
        };

    public GetEndpointLargeResultChunkResponse ReadChunk(int chunkIndex)
    {
        var payload = _payload
            ?? throw new EndpointRpcPayloadException("The endpoint large-result transfer is no longer available.");
        if (chunkIndex != _nextChunkIndex || chunkIndex < 0 || chunkIndex >= ChunkCount)
            throw new EndpointRpcPayloadException("The endpoint large-result chunk sequence is invalid.");

        var offset = checked(chunkIndex * EndpointRpcLimits.MaximumLargeResultChunkBytes);
        var remaining = checked(payload.Length - offset);
        var length = Math.Min(EndpointRpcLimits.MaximumLargeResultChunkBytes, remaining);
        if (length <= 0)
            throw new EndpointRpcPayloadException("The endpoint large-result chunk is invalid.");

        var chunk = new byte[length];
        payload.AsSpan(offset, length).CopyTo(chunk);
        _nextChunkIndex++;
        return new GetEndpointLargeResultChunkResponse
        {
            TransferId = TransferId,
            OriginalCorrelationId = OriginalCorrelationId,
            ChunkIndex = chunkIndex,
            IsFinal = chunkIndex == ChunkCount - 1,
            DeclaredTotalLength = payload.Length,
            DeclaredChunkCount = ChunkCount,
            ChunkPayload = chunk
        };
    }

    public bool IsExpired(DateTimeOffset utcNow) => utcNow >= ExpiresAtUtc;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var payload = Interlocked.Exchange(ref _payload, null);
        EndpointSensitiveData.Clear(payload);
        _releaseCapacity();
    }
}
