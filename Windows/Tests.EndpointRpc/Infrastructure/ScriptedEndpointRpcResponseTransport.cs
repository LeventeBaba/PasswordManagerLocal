using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Security;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class ScriptedEndpointRpcResponseTransport : IEndpointRpcTransport
{
    private readonly Func<long, EndpointRpcTransportResponse> _responseFactory;
    private readonly EndpointRpcTransmissionState _transmissionState;
    private long _correlationId;
    private bool _disposed;

    public ScriptedEndpointRpcResponseTransport(
        Func<long, EndpointRpcTransportResponse> responseFactory,
        EndpointRpcTransmissionState transmissionState = EndpointRpcTransmissionState.Sent)
    {
        _responseFactory = responseFactory ?? throw new ArgumentNullException(nameof(responseFactory));
        if (!Enum.IsDefined(transmissionState))
            throw new ArgumentOutOfRangeException(nameof(transmissionState));
        _transmissionState = transmissionState;
    }

    public bool IsConnected => !_disposed;
    public Task Completion { get; } = Task.Delay(Timeout.InfiniteTimeSpan);
    public int PublicRequestCount { get; private set; }
    public int ChunkRequestCount { get; private set; }
    public int ReleaseRequestCount { get; private set; }

    public Task<EndpointRpcTransportResponse> SendAsync(
        EndpointOperationId operationId,
        byte[] requestPayload,
        bool isMutatingOperation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var correlationId = Interlocked.Increment(ref _correlationId);
        PublicRequestCount++;
        try
        {
            var response = _responseFactory(correlationId);
            if (response.ResponseKind == EndpointRpcResponseKind.LargeResult &&
                response.LargeResult is { } descriptor &&
                descriptor.OriginalCorrelationId != correlationId)
            {
                throw new EndpointRpcCorrelationMismatchException();
            }

            return Task.FromResult(EndpointRpcClientResponseMapper.RequireSuccess(response));
        }
        catch (EndpointRpcRemoteException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new EndpointRpcTransportException(
                correlationId,
                _transmissionState,
                exception);
        }
    }

    public Task<byte[]> GetLargeResultChunkAsync(
        byte[] requestPayload,
        CancellationToken cancellationToken = default)
    {
        ChunkRequestCount++;
        return Task.FromException<byte[]>(new InvalidOperationException(
            "A rejected endpoint response must not start a large-result transfer."));
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
