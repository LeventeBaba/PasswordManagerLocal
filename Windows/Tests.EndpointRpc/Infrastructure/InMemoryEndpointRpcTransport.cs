using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Security;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.EndpointRpc.Server;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

public sealed class InMemoryEndpointRpcTransport : IEndpointRpcTransport
{
    private static readonly Guid ConnectionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid SessionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private readonly EndpointRpcWindowsIpcRequestHandler _handler;
    private readonly EndpointRpcMessageCodec _codec;
    private long _correlationId = 1;
    private bool _disposed;

    public InMemoryEndpointRpcTransport(
        EndpointRpcWindowsIpcRequestHandler handler,
        EndpointRpcMessageCodec codec)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    public bool IsConnected => !_disposed;
    public Task Completion { get; } = Task.Delay(Timeout.InfiniteTimeSpan);
    public int PublicRequestCount { get; private set; }
    public int ChunkRequestCount { get; private set; }
    public int ReleaseRequestCount { get; private set; }

    public Task<EndpointRpcTransportResponse> SendAsync(
        EndpointOperationId operationId,
        byte[] requestPayload,
        EndpointOperationCancellationClassification cancellationClassification,
        CancellationToken cancellationToken = default)
    {
        PublicRequestCount++;
        return SendEncodedAsync(
            _codec.EncodeRequest(operationId, requestPayload),
            cancellationToken);
    }

    public async Task<byte[]> GetLargeResultChunkAsync(
        byte[] requestPayload,
        CancellationToken cancellationToken = default)
    {
        ChunkRequestCount++;
        var response = await SendEncodedAsync(
            _codec.EncodeLargeResultChunkRequest(requestPayload),
            cancellationToken);
        return response.InlinePayload
            ?? throw new InvalidOperationException("The in-memory chunk response was not inline.");
    }

    public async Task ReleaseLargeResultAsync(
        byte[] requestPayload,
        CancellationToken cancellationToken = default)
    {
        ReleaseRequestCount++;
        var response = await SendEncodedAsync(
            _codec.EncodeLargeResultReleaseRequest(requestPayload),
            cancellationToken);
        EndpointSensitiveData.Clear(response.InlinePayload);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private async Task<EndpointRpcTransportResponse> SendEncodedAsync(
        byte[] encodedRequest,
        CancellationToken cancellationToken)
    {
        if (_disposed)
            throw new EndpointRpcDisconnectedException();

        try
        {
            var correlationId = Interlocked.Increment(ref _correlationId);
            var connection = new IpcConnectionContext(
                ConnectionId,
                IpcPeerRole.Ui,
                1234,
                0,
                SessionId,
                IpcCapabilities.EndpointRpc);
            var envelope = new IpcRequestEnvelope(
                correlationId,
                IpcOperationId.EndpointRpcRequest,
                encodedRequest);
            var context = new IpcRequestContext(
                connection,
                envelope,
                new WindowsIpcSerializer());
            var response = await _handler.HandleAsync(context, cancellationToken);
            if (!response.IsSuccess)
                throw new InvalidOperationException("The in-memory IPC handler returned an outer transport failure.");
            return EndpointRpcClientResponseMapper.RequireSuccess(
                _codec.DecodeResponse(response.Result!, correlationId));
        }
        finally
        {
            EndpointSensitiveData.Clear(encodedRequest);
        }
    }
}
