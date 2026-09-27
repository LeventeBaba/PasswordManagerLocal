using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;

namespace PasswordManagerLocal.Windows.EndpointRpc.Validation;

public sealed class EndpointLargeResultContractValidator
{
    public void Validate(EndpointLargeResultDescriptor value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.TransferId == Guid.Empty ||
            value.OriginalCorrelationId <= 0 ||
            value.DeclaredTotalLength <= 0 ||
            value.DeclaredTotalLength > EndpointRpcLimits.MaximumLargeResultTotalBytes ||
            value.DeclaredChunkCount <= 0 ||
            value.DeclaredChunkCount > EndpointRpcLimits.MaximumLargeResultChunkCount ||
            value.ChunkSize <= 0 ||
            value.ChunkSize > EndpointRpcLimits.MaximumLargeResultChunkBytes ||
            value.DeclaredChunkCount != CalculateChunkCount(value.DeclaredTotalLength, value.ChunkSize) ||
            value.ExpiresAtUtc == default)
        {
            throw new EndpointRpcPayloadException("The endpoint large-result descriptor is invalid.");
        }
    }

    public void Validate(GetEndpointLargeResultChunkRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.TransferId == Guid.Empty ||
            value.OriginalCorrelationId <= 0 ||
            value.ChunkIndex < 0 ||
            value.ChunkIndex >= EndpointRpcLimits.MaximumLargeResultChunkCount)
        {
            throw new EndpointRpcPayloadException("The endpoint large-result chunk request is invalid.");
        }
    }

    public void Validate(GetEndpointLargeResultChunkResponse value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.TransferId == Guid.Empty ||
            value.OriginalCorrelationId <= 0 ||
            value.ChunkIndex < 0 ||
            value.DeclaredTotalLength <= 0 ||
            value.DeclaredTotalLength > EndpointRpcLimits.MaximumLargeResultTotalBytes ||
            value.DeclaredChunkCount <= 0 ||
            value.DeclaredChunkCount > EndpointRpcLimits.MaximumLargeResultChunkCount ||
            value.ChunkIndex >= value.DeclaredChunkCount ||
            value.ChunkPayload is null ||
            value.ChunkPayload.Length <= 0 ||
            value.ChunkPayload.Length > EndpointRpcLimits.MaximumLargeResultChunkBytes ||
            value.IsFinal != (value.ChunkIndex == value.DeclaredChunkCount - 1))
        {
            throw new EndpointRpcPayloadException("The endpoint large-result chunk response is invalid.");
        }
    }

    public void Validate(ReleaseEndpointLargeResultRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.TransferId == Guid.Empty || value.OriginalCorrelationId <= 0)
            throw new EndpointRpcPayloadException("The endpoint large-result release request is invalid.");
    }

    public void Validate(ReleaseEndpointLargeResultResponse value) =>
        ArgumentNullException.ThrowIfNull(value);

    private static int CalculateChunkCount(int totalLength, int chunkSize) =>
        checked((totalLength + chunkSize - 1) / chunkSize);
}
