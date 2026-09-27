using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class ScriptedLargeResultTransport : IEndpointRpcTransport
{
    private readonly EndpointLargeResultDescriptor _descriptor;
    private readonly Func<GetEndpointLargeResultChunkRequest, GetEndpointLargeResultChunkResponse> _chunkFactory;
    private readonly EndpointRpcSerializer _serializer = new();
    private bool _disposed;

    public ScriptedLargeResultTransport(
        EndpointLargeResultDescriptor descriptor,
        Func<GetEndpointLargeResultChunkRequest, GetEndpointLargeResultChunkResponse> chunkFactory)
    {
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _chunkFactory = chunkFactory ?? throw new ArgumentNullException(nameof(chunkFactory));
    }

    public bool IsConnected => !_disposed;
    public Task Completion { get; } = Task.Delay(Timeout.InfiniteTimeSpan);
    public int PublicRequestCount { get; private set; }
    public int ChunkRequestCount { get; private set; }
    public int ReleaseRequestCount { get; private set; }
    public List<byte[]> ReturnedPayloads { get; } = [];

    public Task<EndpointRpcTransportResponse> SendAsync(
        EndpointOperationId operationId,
        byte[] requestPayload,
        EndpointOperationCancellationClassification cancellationClassification,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PublicRequestCount++;
        return Task.FromResult(EndpointRpcTransportResponse.Large(_descriptor));
    }

    public Task<byte[]> GetLargeResultChunkAsync(
        byte[] requestPayload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChunkRequestCount++;
        var request = _serializer.Deserialize(
            requestPayload,
            EndpointRpcJsonContext.Default.GetEndpointLargeResultChunkRequest);
        var response = _chunkFactory(request);
        try
        {
            var payload = _serializer.Serialize(
                response,
                EndpointRpcJsonContext.Default.GetEndpointLargeResultChunkResponse);
            ReturnedPayloads.Add(payload);
            return Task.FromResult(payload);
        }
        finally
        {
            Array.Clear(response.ChunkPayload);
        }
    }

    public Task ReleaseLargeResultAsync(
        byte[] requestPayload,
        CancellationToken cancellationToken = default)
    {
        ReleaseRequestCount++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
