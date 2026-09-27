using PasswordManagerLocal.Windows.EndpointRpc.Contracts;

namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

public interface IEndpointRpcTransport : IAsyncDisposable
{
    bool IsConnected { get; }
    Task Completion { get; }

    Task<EndpointRpcTransportResponse> SendAsync(
        EndpointOperationId operationId,
        byte[] requestPayload,
        EndpointOperationCancellationClassification cancellationClassification,
        CancellationToken cancellationToken = default);

    Task<byte[]> GetLargeResultChunkAsync(
        byte[] requestPayload,
        CancellationToken cancellationToken = default);

    Task ReleaseLargeResultAsync(
        byte[] requestPayload,
        CancellationToken cancellationToken = default);
}
