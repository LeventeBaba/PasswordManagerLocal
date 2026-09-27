using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class RecordingEndpointRpcTransport : IEndpointRpcTransport
{
    private readonly Func<EndpointOperationId, byte[], CancellationToken, Task<byte[]>> _handler;
    private readonly EndpointRpcTransmissionState _failureTransmissionState;
    private long _correlationId;
    private bool _disposed;

    public RecordingEndpointRpcTransport(
        Func<EndpointOperationId, byte[], CancellationToken, Task<byte[]>> handler,
        EndpointRpcTransmissionState failureTransmissionState = EndpointRpcTransmissionState.Sent)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        if (!Enum.IsDefined(failureTransmissionState))
            throw new ArgumentOutOfRangeException(nameof(failureTransmissionState));
        _failureTransmissionState = failureTransmissionState;
    }

    public List<EndpointOperationId> Operations { get; } = [];
    public List<byte[]> RequestPayloads { get; } = [];
    public bool IsConnected => !_disposed;
    public Task Completion { get; } = Task.Delay(Timeout.InfiniteTimeSpan);

    public async Task<EndpointRpcTransportResponse> SendAsync(
        EndpointOperationId operationId,
        byte[] requestPayload,
        EndpointOperationCancellationClassification cancellationClassification,
        CancellationToken cancellationToken = default)
    {
        var correlationId = Interlocked.Increment(ref _correlationId);
        if (_disposed)
        {
            throw new EndpointRpcTransportException(
                correlationId,
                EndpointRpcTransmissionState.DefinitelyNotSent,
                new EndpointRpcDisconnectedException());
        }

        Operations.Add(operationId);
        RequestPayloads.Add(requestPayload.ToArray());
        try
        {
            return EndpointRpcTransportResponse.Inline(
                await _handler(operationId, requestPayload, cancellationToken));
        }
        catch (Exception exception)
            when (exception is not EndpointRpcRemoteException and
                not EndpointRpcTransportException)
        {
            throw new EndpointRpcTransportException(
                correlationId,
                _failureTransmissionState,
                exception);
        }
    }

    public Task<byte[]> GetLargeResultChunkAsync(
        byte[] requestPayload,
        CancellationToken cancellationToken = default) =>
        Task.FromException<byte[]>(new NotSupportedException());

    public Task ReleaseLargeResultAsync(
        byte[] requestPayload,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
