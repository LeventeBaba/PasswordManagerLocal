using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Security;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;

namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

public sealed class WindowsEndpointRpcTransport : IEndpointRpcTransport
{
    private readonly WindowsIpcClient _client;
    private readonly EndpointRpcMessageCodec _messageCodec;
    private int _disposed;

    public WindowsEndpointRpcTransport(
        WindowsIpcClient client,
        EndpointRpcMessageCodec messageCodec)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _messageCodec = messageCodec ?? throw new ArgumentNullException(nameof(messageCodec));
    }

    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _client.IsConnected;
    public Task Completion => _client.Completion;

    public Task<EndpointRpcTransportResponse> SendAsync(
        EndpointOperationId operationId,
        byte[] requestPayload,
        EndpointOperationCancellationClassification cancellationClassification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestPayload);
        if (!Enum.IsDefined(cancellationClassification))
            throw new ArgumentOutOfRangeException(nameof(cancellationClassification));

        return SendCoreAsync(
            () => _messageCodec.EncodeRequest(operationId, requestPayload),
            cancellationClassification != EndpointOperationCancellationClassification.ReadOnlySafelyCancellable,
            cancellationToken);
    }

    public async Task<byte[]> GetLargeResultChunkAsync(
        byte[] requestPayload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestPayload);
        var response = await SendCoreAsync(
            () => _messageCodec.EncodeLargeResultChunkRequest(requestPayload),
            treatPostHandlerFailureAsUncertain: false,
            cancellationToken);
        if (response.ResponseKind != EndpointRpcResponseKind.InlineResult ||
            response.InlinePayload is null)
        {
            throw new EndpointRpcTransportException(
                0,
                EndpointRpcTransmissionState.Sent,
                new EndpointRpcPayloadException("The endpoint large-result chunk response is invalid."));
        }

        return response.InlinePayload;
    }

    public async Task ReleaseLargeResultAsync(
        byte[] requestPayload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestPayload);
        var response = await SendCoreAsync(
            () => _messageCodec.EncodeLargeResultReleaseRequest(requestPayload),
            treatPostHandlerFailureAsUncertain: false,
            cancellationToken);
        if (response.ResponseKind != EndpointRpcResponseKind.InlineResult ||
            response.InlinePayload is null)
        {
            throw new EndpointRpcTransportException(
                0,
                EndpointRpcTransmissionState.Sent,
                new EndpointRpcPayloadException("The endpoint large-result release response is invalid."));
        }
        EndpointSensitiveData.Clear(response.InlinePayload);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _client.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async Task<EndpointRpcTransportResponse> SendCoreAsync(
        Func<byte[]> encodeRequest,
        bool treatPostHandlerFailureAsUncertain,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(encodeRequest);
        ThrowIfDisposed();
        if (!IsConnected)
        {
            throw new EndpointRpcTransportException(
                0,
                EndpointRpcTransmissionState.DefinitelyNotSent,
                new EndpointRpcDisconnectedException());
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new EndpointRpcTransportException(
                0,
                EndpointRpcTransmissionState.DefinitelyNotSent,
                new OperationCanceledException(cancellationToken));
        }

        byte[]? encodedRequest = null;
        byte[]? encodedResponse = null;
        long correlationId = 0;
        try
        {
            try
            {
                encodedRequest = encodeRequest();
            }
            catch (Exception exception)
            {
                throw new EndpointRpcTransportException(
                    0,
                    EndpointRpcTransmissionState.DefinitelyNotSent,
                    exception);
            }

            IpcTransmissionAwareResponse response;
            try
            {
                response = await _client.SendWithTransmissionStateAsync(
                    IpcOperationId.EndpointRpcRequest,
                    encodedRequest,
                    cancellationToken);
                correlationId = response.CorrelationId;
            }
            catch (IpcRequestTransmissionException exception)
            {
                throw new EndpointRpcTransportException(
                    exception.CorrelationId,
                    MapTransmissionState(exception.TransmissionState),
                    MapTransportFailure(exception.InnerException ?? exception));
            }
            catch (IpcRemoteException exception)
            {
                var mapped = EndpointRpcTransportErrorMapper.Map(exception.Error);
                if (treatPostHandlerFailureAsUncertain &&
                    IsPostHandlerFailure(exception.Error.ErrorCode))
                {
                    throw new EndpointRpcTransportException(
                        exception.Error.CorrelationId,
                        EndpointRpcTransmissionState.Sent,
                        mapped);
                }

                throw mapped;
            }

            encodedResponse = response.Response.Result
                ?? throw new EndpointRpcTransportException(
                    correlationId,
                    EndpointRpcTransmissionState.Sent,
                    new EndpointRpcPayloadException("The endpoint RPC response payload is missing."));
            try
            {
                return EndpointRpcClientResponseMapper.RequireSuccess(
                    _messageCodec.DecodeResponse(encodedResponse, correlationId));
            }
            catch (EndpointRpcRemoteException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new EndpointRpcTransportException(
                    correlationId,
                    EndpointRpcTransmissionState.Sent,
                    exception);
            }
        }
        finally
        {
            EndpointSensitiveData.Clear(encodedRequest);
            EndpointSensitiveData.Clear(encodedResponse);
        }
    }

    internal static bool IsPostHandlerFailure(IpcErrorCode errorCode) => errorCode is
        IpcErrorCode.HandlerFailed or
        IpcErrorCode.InvalidEnvelope or
        IpcErrorCode.InvalidPayload or
        IpcErrorCode.RequestCancelled or
        IpcErrorCode.InternalFailure or
        IpcErrorCode.ResponsePayloadTooLarge or
        IpcErrorCode.SerializedEnvelopeTooLarge;

    private static EndpointRpcTransmissionState MapTransmissionState(
        IpcRequestTransmissionState state) => state switch
    {
        IpcRequestTransmissionState.DefinitelyNotSent => EndpointRpcTransmissionState.DefinitelyNotSent,
        IpcRequestTransmissionState.Sent => EndpointRpcTransmissionState.Sent,
        IpcRequestTransmissionState.TransmissionUnknown => EndpointRpcTransmissionState.TransmissionUnknown,
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static Exception MapTransportFailure(Exception exception) => exception switch
    {
        PasswordManagerLocal.Windows.Ipc.Transport.IpcConnectionClosedException =>
            new EndpointRpcDisconnectedException(exception),
        _ => exception
    };

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WindowsEndpointRpcTransport));
    }
}
