using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;
using System.Buffers.Binary;

namespace PasswordManagerLocal.Windows.EndpointRpc.Serialization;

/// <summary>
/// Encodes endpoint messages inside the IPC request/response payload. The v5
/// IPC response discriminator owns the response kind, so endpoint responses
/// contain only their JSON body.
/// </summary>
public sealed class EndpointRpcMessageCodec
{
    private const int OperationIdPrefixSize = sizeof(int);
    private const int LargeResultChunkOperationId = -1;
    private const int LargeResultReleaseOperationId = -2;
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
        return EncodeRequestCore((int)operationId, payload);
    }

    public byte[] EncodeLargeResultChunkRequest(ReadOnlySpan<byte> payload) =>
        EncodeRequestCore(LargeResultChunkOperationId, payload);

    public byte[] EncodeLargeResultReleaseRequest(ReadOnlySpan<byte> payload) =>
        EncodeRequestCore(LargeResultReleaseOperationId, payload);

    public EndpointRpcDecodedRequest DecodeRequest(ReadOnlySpan<byte> message)
    {
        if (message.Length < OperationIdPrefixSize ||
            message.Length - OperationIdPrefixSize > EndpointRpcLimits.MaximumRequestPayloadSize)
        {
            throw new EndpointRpcPayloadException("The endpoint RPC request message is invalid.");
        }

        var rawOperationId = BinaryPrimitives.ReadInt32BigEndian(message[..OperationIdPrefixSize]);
        var body = message[OperationIdPrefixSize..];
        if (rawOperationId == LargeResultChunkOperationId)
            return new EndpointRpcDecodedRequest(EndpointRpcRequestKind.GetLargeResultChunk, null, body.ToArray());
        if (rawOperationId == LargeResultReleaseOperationId)
            return new EndpointRpcDecodedRequest(EndpointRpcRequestKind.ReleaseLargeResult, null, body.ToArray());
        if (rawOperationId <= 0 || !Enum.IsDefined((EndpointOperationId)rawOperationId))
            throw new EndpointRpcPayloadException("The endpoint RPC operation ID is invalid.");

        return new EndpointRpcDecodedRequest(
            EndpointRpcRequestKind.PublicOperation,
            (EndpointOperationId)rawOperationId,
            body.ToArray());
    }

    public byte[] EncodeSuccess(ReadOnlySpan<byte> payload) =>
        CopyBounded(payload, EndpointRpcLimits.MaximumResponsePayloadSize);

    public byte[] EncodeLargeResult(EndpointLargeResultDescriptor descriptor)
    {
        _largeResultValidator.Validate(descriptor);
        var payload = _serializer.Serialize(
            descriptor,
            EndpointRpcJsonContext.Default.EndpointLargeResultDescriptor);
        try
        {
            return CopyBounded(payload, EndpointRpcLimits.MaximumSmallResponsePayloadSize);
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
            return CopyBounded(payload, EndpointRpcLimits.MaximumErrorPayloadSize);
        }
        finally
        {
            Array.Clear(payload);
        }
    }

    public EndpointRpcError DecodeFailure(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || payload.Length > EndpointRpcLimits.MaximumErrorPayloadSize)
            throw new EndpointRpcPayloadException("The endpoint RPC error payload is invalid.");
        var error = _serializer.Deserialize(payload, EndpointRpcJsonContext.Default.EndpointRpcError);
        _validator.Validate(error);
        return error;
    }

    public EndpointLargeResultDescriptor DecodeLargeResult(
        ReadOnlySpan<byte> payload,
        long expectedCorrelationId)
    {
        if (expectedCorrelationId <= 0 || payload.IsEmpty ||
            payload.Length > EndpointRpcLimits.MaximumSmallResponsePayloadSize)
        {
            throw new EndpointRpcPayloadException("The endpoint large-result descriptor payload is invalid.");
        }

        var descriptor = _serializer.Deserialize(
            payload,
            EndpointRpcJsonContext.Default.EndpointLargeResultDescriptor);
        _largeResultValidator.Validate(descriptor);
        if (descriptor.OriginalCorrelationId != expectedCorrelationId)
            throw new EndpointRpcCorrelationMismatchException();
        return descriptor;
    }

    private static byte[] EncodeRequestCore(int operationId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > EndpointRpcLimits.MaximumRequestPayloadSize)
            throw new EndpointRpcPayloadException("The endpoint RPC request payload exceeds the permitted size.");

        var result = new byte[checked(OperationIdPrefixSize + payload.Length)];
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(0, OperationIdPrefixSize), operationId);
        payload.CopyTo(result.AsSpan(OperationIdPrefixSize));
        return result;
    }

    private static byte[] CopyBounded(ReadOnlySpan<byte> payload, int maximumPayloadSize)
    {
        if (payload.Length > maximumPayloadSize)
            throw new EndpointRpcPayloadException("The endpoint RPC response payload exceeds the permitted size.");
        return payload.ToArray();
    }
}
