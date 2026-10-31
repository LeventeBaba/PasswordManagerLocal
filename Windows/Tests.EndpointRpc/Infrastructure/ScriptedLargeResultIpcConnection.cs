using System.Threading.Channels;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

/// <summary>
/// Scripts peer frames only. The real IPC client and endpoint transport must
/// validate the descriptor's original request correlation and classify failures.
/// </summary>
public sealed class ScriptedLargeResultIpcConnection : IWindowsIpcConnection
{
    private readonly Channel<IpcFrame> _responses = Channel.CreateUnbounded<IpcFrame>();
    private readonly WindowsIpcSerializer _ipcSerializer = new();
    private readonly EndpointRpcSerializer _endpointSerializer = new();
    private readonly EndpointRpcMessageCodec _endpointCodec;
    private readonly IpcFramePayloadCodec _payloadCodec;
    private readonly bool _mismatchCorrelation;
    private readonly byte[] _resultPayload;
    private EndpointLargeResultDescriptor? _descriptor;
    private bool _disposed;

    public ScriptedLargeResultIpcConnection(bool mismatchCorrelation)
    {
        _mismatchCorrelation = mismatchCorrelation;
        _endpointCodec = new EndpointRpcMessageCodec(_endpointSerializer);
        _payloadCodec = new IpcFramePayloadCodec(_ipcSerializer);
        _resultPayload = _endpointSerializer.Serialize(new GetSavedPasswordsEndpointResponse(),
            EndpointRpcJsonContext.Default.GetSavedPasswordsEndpointResponse);
    }

    public Guid ConnectionId { get; } = Guid.NewGuid();
    public int? VerifiedPeerProcessId => null;
    public bool IsConnected => !_disposed;
    public int PublicRequestCount { get; private set; }
    public int ChunkRequestCount { get; private set; }
    public int ReleaseRequestCount { get; private set; }

    public async ValueTask<IpcFrame?> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        while (await _responses.Reader.WaitToReadAsync(cancellationToken))
            if (_responses.Reader.TryRead(out var frame))
                return frame;
        return null;
    }

    public ValueTask WriteFrameAsync(IpcFrame frame, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
            throw new ObjectDisposedException(nameof(ScriptedLargeResultIpcConnection));

        if (frame.Header.MessageKind == IpcMessageKind.HandshakeRequest)
        {
            var handshake = new IpcHandshakeResponse(true, IpcPeerRole.Agent, ConnectionId,
                IpcCapabilities.EndpointRpc, null);
            QueueFrame(IpcMessageKind.HandshakeResponse, frame.Header.CorrelationId,
                _ipcSerializer.Serialize(handshake, WindowsIpcJsonContext.Default.IpcHandshakeResponse));
            return ValueTask.CompletedTask;
        }

        var request = _payloadCodec.DecodeRequest(frame.Header.CorrelationId, frame.Payload);
        var endpoint = _endpointCodec.DecodeRequest(request.Payload!);
        IpcResponseEnvelope response;
        switch (endpoint.RequestKind)
        {
            case EndpointRpcRequestKind.PublicOperation:
                PublicRequestCount++;
                _descriptor = new EndpointLargeResultDescriptor
                {
                    TransferId = Guid.NewGuid(),
                    OriginalCorrelationId = frame.Header.CorrelationId + (_mismatchCorrelation ? 1 : 0),
                    DeclaredTotalLength = _resultPayload.Length,
                    DeclaredChunkCount = 1,
                    ChunkSize = _resultPayload.Length,
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1)
                };
                response = IpcResponseEnvelope.LargeResult(_endpointCodec.EncodeLargeResult(_descriptor));
                break;
            case EndpointRpcRequestKind.GetLargeResultChunk:
                ChunkRequestCount++;
                var descriptor = _descriptor ?? throw new InvalidOperationException("No descriptor was sent.");
                response = IpcResponseEnvelope.Success(_endpointSerializer.Serialize(
                    new GetEndpointLargeResultChunkResponse
                    {
                        TransferId = descriptor.TransferId,
                        OriginalCorrelationId = descriptor.OriginalCorrelationId,
                        ChunkIndex = 0,
                        IsFinal = true,
                        DeclaredTotalLength = _resultPayload.Length,
                        DeclaredChunkCount = 1,
                        ChunkPayload = _resultPayload
                    }, EndpointRpcJsonContext.Default.GetEndpointLargeResultChunkResponse));
                break;
            case EndpointRpcRequestKind.ReleaseLargeResult:
                ReleaseRequestCount++;
                response = IpcResponseEnvelope.Success(_endpointSerializer.Serialize(
                    new ReleaseEndpointLargeResultResponse { Released = true },
                    EndpointRpcJsonContext.Default.ReleaseEndpointLargeResultResponse));
                break;
            default:
                throw new InvalidOperationException("Unexpected scripted endpoint request.");
        }

        QueueFrame(IpcMessageKind.Response, frame.Header.CorrelationId,
            _payloadCodec.EncodeResponse(response, frame.Header.CorrelationId));
        return ValueTask.CompletedTask;
    }

    public async ValueTask<IpcFrameWriteResult> TryWriteFrameAsync(
        IpcFrame frame, Func<bool> tryBeginWrite,
        CancellationToken queuedCancellationToken, CancellationToken shutdownCancellationToken)
    {
        queuedCancellationToken.ThrowIfCancellationRequested();
        shutdownCancellationToken.ThrowIfCancellationRequested();
        if (!tryBeginWrite())
            return IpcFrameWriteResult.SkippedBeforeTransmission;
        await WriteFrameAsync(frame, shutdownCancellationToken);
        return IpcFrameWriteResult.Written;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        _responses.Writer.TryComplete();
        Array.Clear(_resultPayload);
        return ValueTask.CompletedTask;
    }

    private void QueueFrame(IpcMessageKind kind, long correlationId, byte[] payload)
    {
        var frame = new IpcFrame(new IpcFrameHeader(WindowsIpcProtocol.CurrentVersion,
            kind, correlationId, payload.Length), payload);
        if (!_responses.Writer.TryWrite(frame))
            throw new ObjectDisposedException(nameof(ScriptedLargeResultIpcConnection));
    }
}
