using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;
using System.Buffers.Binary;

namespace PasswordManagerLocal.Windows.EndpointRpc.Serialization;

public sealed class EndpointRpcMessageCodec
{
    private const int RequestHeaderSize = 5;
    private const int ResponseHeaderSize = 1;
    private readonly EndpointRpcSerializer _serializer;
    private readonly EndpointRpcContractValidator _validator;
    private readonly EndpointLargeResultContractValidator _largeResultValidator;

    public EndpointRpcMessageCodec(EndpointRpcSerializer serializer)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _validator = new EndpointRpcContractValidator();
        _largeResultValidator = new EndpointLargeResultContractValidator();
    }

    public byte[] EncodeRequest(EndpointOperationId operationId, ReadOnlySpan<byte> payload)
    {
        if (!Enum.IsDefined(operationId))
            throw new ArgumentOutOfRangeException(nameof(operationId));
        return EncodeRequestCore(EndpointRpcRequestKind.PublicOperation, (int)operationId, payload);
    }

    public byte[] EncodeLargeResultChunkRequest(ReadOnlySpan<byte> payload) =>
        EncodeRequestCore(EndpointRpcRequestKind.GetLargeResultChunk, 0, payload);

    public byte[] EncodeLargeResultReleaseRequest(ReadOnlySpan<byte> payload) =>
        EncodeRequestCore(EndpointRpcRequestKind.ReleaseLargeResult, 0, payload);

    public EndpointRpcDecodedRequest DecodeRequest(ReadOnlySpan<byte> message)
    {
        if (message.Length < RequestHeaderSize ||
            message.Length - RequestHeaderSize > EndpointRpcLimits.MaximumRequestPayloadSize)
        {
            throw new EndpointRpcPayloadException("The endpoint RPC request message is invalid.");
        }

        var requestKind = (EndpointRpcRequestKind)message[0];
        if (!Enum.IsDefined(requestKind))
            throw new EndpointRpcPayloadException("The endpoint RPC request kind is invalid.");
        var rawOperationId = BinaryPrimitives.ReadInt32BigEndian(message.Slice(1, 4));
        EndpointOperationId? operationId = null;
        if (requestKind == EndpointRpcRequestKind.PublicOperation)
        {
            operationId = (EndpointOperationId)rawOperationId;
        }
        else if (rawOperationId != 0)
        {
            throw new EndpointRpcPayloadException("The endpoint RPC internal request is invalid.");
        }

        return new EndpointRpcDecodedRequest(
            requestKind,
            operationId,
            message[RequestHeaderSize..].ToArray());
    }

    public byte[] EncodeSuccess(ReadOnlySpan<byte> payload) =>
        EncodeResponse(EndpointRpcResponseKind.InlineResult, payload, EndpointRpcLimits.MaximumResponsePayloadSize);

    public byte[] EncodeLargeResult(EndpointLargeResultDescriptor descriptor)
    {
        _largeResultValidator.Validate(descriptor);
        var payload = _serializer.Serialize(
            descriptor,
            EndpointRpcJsonContext.Default.EndpointLargeResultDescriptor);
        try
        {
            return EncodeResponse(
                EndpointRpcResponseKind.LargeResult,
                payload,
                EndpointRpcLimits.MaximumSmallResponsePayloadSize);
        }
        finally
        {
            Array.Clear(payload);
        }
    }

    public byte[] EncodeFailure(EndpointRpcError error)
    {
        _validator.Validate(error);
        var payload = _serializer.Serialize(error, EndpointRpcJsonContext.Default.EndpointRpcError);
        try
        {
            return EncodeResponse(
                EndpointRpcResponseKind.Failure,
                payload,
                EndpointRpcLimits.MaximumErrorPayloadSize);
        }
        finally
        {
            Array.Clear(payload);
        }
    }

    public EndpointRpcTransportResponse DecodeResponse(
        ReadOnlySpan<byte> message,
        long expectedCorrelationId)
    {
        if (expectedCorrelationId <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedCorrelationId));
        if (message.Length < ResponseHeaderSize ||
            message.Length - ResponseHeaderSize > EndpointRpcLimits.MaximumResponsePayloadSize)
        {
            throw new EndpointRpcPayloadException("The endpoint RPC response message is invalid.");
        }

        var responseKind = (EndpointRpcResponseKind)message[0];
        if (!Enum.IsDefined(responseKind))
            throw new EndpointRpcPayloadException("The endpoint RPC response status is invalid.");
        var payload = message[ResponseHeaderSize..];
        if (responseKind == EndpointRpcResponseKind.InlineResult)
            return EndpointRpcTransportResponse.Inline(payload.ToArray());
        if (responseKind == EndpointRpcResponseKind.LargeResult)
        {
            var descriptor = _serializer.Deserialize(
                payload,
                EndpointRpcJsonContext.Default.EndpointLargeResultDescriptor);
            _largeResultValidator.Validate(descriptor);
            ValidateCorrelation(expectedCorrelationId, descriptor.OriginalCorrelationId);
            return EndpointRpcTransportResponse.Large(descriptor);
        }
        if (payload.Length > EndpointRpcLimits.MaximumErrorPayloadSize)
            throw new EndpointRpcPayloadException("The endpoint RPC error payload exceeds the permitted size.");

        var error = _serializer.Deserialize(payload, EndpointRpcJsonContext.Default.EndpointRpcError);
        _validator.Validate(error);
        ValidateCorrelation(expectedCorrelationId, error.CorrelationId);
        return EndpointRpcTransportResponse.Failure(error);
    }

    private static void ValidateCorrelation(long expectedCorrelationId, long nestedCorrelationId)
    {
        if (nestedCorrelationId != expectedCorrelationId)
            throw new EndpointRpcCorrelationMismatchException();
    }

    private static byte[] EncodeRequestCore(
        EndpointRpcRequestKind requestKind,
        int operationId,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length > EndpointRpcLimits.MaximumRequestPayloadSize)
            throw new EndpointRpcPayloadException("The endpoint RPC request payload exceeds the permitted size.");

        var result = new byte[RequestHeaderSize + payload.Length];
        result[0] = (byte)requestKind;
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(1, 4), operationId);
        payload.CopyTo(result.AsSpan(RequestHeaderSize));
        return result;
    }

    private static byte[] EncodeResponse(
        EndpointRpcResponseKind responseKind,
        ReadOnlySpan<byte> payload,
        int maximumPayloadSize)
    {
        if (payload.Length > maximumPayloadSize)
            throw new EndpointRpcPayloadException("The endpoint RPC response payload exceeds the permitted size.");

        var result = new byte[ResponseHeaderSize + payload.Length];
        result[0] = (byte)responseKind;
        payload.CopyTo(result.AsSpan(ResponseHeaderSize));
        return result;
    }
}
