using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Validation;
using System.Buffers.Binary;

namespace PasswordManagerLocal.Windows.Ipc.Serialization;

public sealed class IpcFramePayloadCodec
{

    private readonly WindowsIpcSerializer _serializer;
    private readonly WindowsIpcContractValidator _validator;

    public IpcFramePayloadCodec(
        WindowsIpcSerializer serializer,
        WindowsIpcContractValidator? validator = null)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _validator = validator ?? new WindowsIpcContractValidator();
    }

    public byte[] EncodeRequest(IpcRequestEnvelope request, long correlationId)
    {
        ArgumentNullException.ThrowIfNull(request);
        _validator.Validate(request, correlationId);

        var innerPayload = request.Payload ?? Array.Empty<byte>();
        var length = checked(SerializationConstants.IpcFramePayloadCodecOperationIdPrefixSize + innerPayload.Length);
        if (length > WindowsIpcProtocol.MaximumPayloadSize)
        {
            throw new IpcPayloadLimitExceededException(
                IpcErrorCode.RequestPayloadTooLarge,
                "The IPC request payload exceeds the permitted frame size.",
                correlationId);
        }

        var payload = new byte[length];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, SerializationConstants.IpcFramePayloadCodecOperationIdPrefixSize), (int)request.OperationId);
        innerPayload.AsSpan().CopyTo(payload.AsSpan(SerializationConstants.IpcFramePayloadCodecOperationIdPrefixSize));
        return payload;
    }

    public IpcRequestEnvelope DecodeRequest(long correlationId, ReadOnlySpan<byte> payload)
    {
        if (correlationId <= 0 || payload.Length < SerializationConstants.IpcFramePayloadCodecOperationIdPrefixSize)
            throw new IpcPayloadException("The IPC request payload is invalid.");

        var rawOperationId = BinaryPrimitives.ReadInt32BigEndian(payload[..SerializationConstants.IpcFramePayloadCodecOperationIdPrefixSize]);
        var operationId = (IpcOperationId)rawOperationId;
        var innerPayload = payload[SerializationConstants.IpcFramePayloadCodecOperationIdPrefixSize..];
        if (!Enum.IsDefined(operationId))
            throw new IpcPayloadException("The IPC request operation ID is invalid.");
        if (innerPayload.Length > IpcContractLimits.MaximumInnerPayloadSize)
        {
            throw new IpcPayloadLimitExceededException(
                IpcErrorCode.RequestPayloadTooLarge,
                "The IPC request payload exceeds the permitted size.",
                correlationId);
        }

        var request = new IpcRequestEnvelope(
            operationId,
            innerPayload.IsEmpty ? null : innerPayload.ToArray());
        _validator.Validate(request, correlationId);
        return request;
    }

    public byte[] EncodeResponse(IpcResponseEnvelope response, long correlationId)
    {
        ArgumentNullException.ThrowIfNull(response);
        _validator.Validate(response, correlationId);

        byte[]? errorPayload = null;
        ReadOnlySpan<byte> body;
        switch (response.PayloadKind)
        {
            case IpcResponsePayloadKind.Success:
                body = response.Result ?? Array.Empty<byte>();
                break;
            case IpcResponsePayloadKind.Failure:
                errorPayload = _serializer.Serialize(
                    response.Error!,
                    WindowsIpcJsonContext.Default.IpcError);
                if (errorPayload.Length > IpcContractLimits.MaximumErrorPayloadSize)
                {
                    Array.Clear(errorPayload);
                    throw new IpcPayloadLimitExceededException(
                        IpcErrorCode.ResponsePayloadTooLarge,
                        "The IPC error payload exceeds the permitted size.",
                        correlationId);
                }
                body = errorPayload;
                break;
            case IpcResponsePayloadKind.EndpointFailure:
                body = response.Result!;
                if (body.Length > IpcContractLimits.MaximumErrorPayloadSize)
                {
                    throw new IpcPayloadLimitExceededException(
                        IpcErrorCode.ResponsePayloadTooLarge,
                        "The endpoint error payload exceeds the permitted size.",
                        correlationId);
                }
                break;
            case IpcResponsePayloadKind.LargeResult:
                body = response.Result!;
                break;
            default:
                throw new IpcPayloadException("The IPC response payload kind is invalid.");
        }

        var length = checked(SerializationConstants.IpcFramePayloadCodecResponseKindPrefixSize + body.Length);
        if (length > WindowsIpcProtocol.MaximumPayloadSize)
        {
            if (errorPayload is not null)
                Array.Clear(errorPayload);
            throw new IpcPayloadLimitExceededException(
                IpcErrorCode.ResponsePayloadTooLarge,
                "The IPC response payload exceeds the permitted frame size.",
                correlationId);
        }

        var payload = new byte[length];
        payload[0] = (byte)response.PayloadKind;
        body.CopyTo(payload.AsSpan(SerializationConstants.IpcFramePayloadCodecResponseKindPrefixSize));
        if (errorPayload is not null)
            Array.Clear(errorPayload);
        return payload;
    }

    public IpcResponseEnvelope DecodeResponse(long correlationId, ReadOnlySpan<byte> payload)
    {
        if (correlationId <= 0 || payload.Length < SerializationConstants.IpcFramePayloadCodecResponseKindPrefixSize)
            throw new IpcPayloadException("The IPC response payload is invalid.");

        var payloadKind = (IpcResponsePayloadKind)payload[0];
        if (!Enum.IsDefined(payloadKind))
            throw new IpcPayloadException("The IPC response payload kind is invalid.");

        var body = payload[SerializationConstants.IpcFramePayloadCodecResponseKindPrefixSize..];
        if (payloadKind == IpcResponsePayloadKind.Failure)
        {
            if (body.IsEmpty || body.Length > IpcContractLimits.MaximumErrorPayloadSize)
                throw new IpcPayloadException("The IPC error payload is invalid.");
            var error = _serializer.Deserialize(body, WindowsIpcJsonContext.Default.IpcError);
            _validator.Validate(error);
            return IpcResponseEnvelope.Failure(error);
        }

        if (body.Length > IpcContractLimits.MaximumInnerPayloadSize)
            throw new IpcPayloadException("The IPC response payload exceeds the permitted size.");

        if (payloadKind == IpcResponsePayloadKind.EndpointFailure &&
            (body.IsEmpty || body.Length > IpcContractLimits.MaximumErrorPayloadSize))
        {
            throw new IpcPayloadException("The endpoint error payload is invalid.");
        }

        var result = body.IsEmpty ? null : body.ToArray();
        return payloadKind switch
        {
            IpcResponsePayloadKind.Success => IpcResponseEnvelope.Success(result),
            IpcResponsePayloadKind.EndpointFailure => IpcResponseEnvelope.EndpointFailure(
                result ?? throw new IpcPayloadException("The endpoint error payload is empty.")),
            IpcResponsePayloadKind.LargeResult => IpcResponseEnvelope.LargeResult(
                result ?? throw new IpcPayloadException("The large-result descriptor payload is empty.")),
            _ => throw new IpcPayloadException("The IPC response payload kind is invalid.")
        };
    }
}
