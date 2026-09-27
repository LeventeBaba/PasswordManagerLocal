using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Authorization;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;
using PasswordManagerLocal.Windows.EndpointRpc.Metadata;
using PasswordManagerLocal.Windows.EndpointRpc.Security;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.EndpointRpc.Server.LargeTransfer;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.EndpointRpc.Server;

public sealed class EndpointRpcWindowsIpcRequestHandler : IWindowsIpcRequestHandler
{
    private readonly EndpointRpcDispatcher _dispatcher;
    private readonly EndpointRpcMessageCodec _messageCodec;
    private readonly EndpointRpcContractValidator _validator;
    private readonly EndpointRpcSerializer _serializer;
    private readonly EndpointLargeResultContractValidator _largeResultValidator;
    private readonly EndpointLargeResultTransferStore _largeResultTransferStore;
    private readonly EndpointRpcBackendErrorMapper _errorMapper;
    private readonly IEndpointRpcAdmissionPolicy _admissionPolicy;

    public EndpointRpcWindowsIpcRequestHandler(
        EndpointRpcDispatcher dispatcher,
        EndpointRpcMessageCodec messageCodec,
        EndpointRpcContractValidator validator,
        IEndpointRpcAdmissionPolicy admissionPolicy)
        : this(
            dispatcher,
            messageCodec,
            validator,
            new EndpointRpcSerializer(),
            dispatcher.LargeResultTransferStore,
            new EndpointRpcBackendErrorMapper(),
            admissionPolicy)
    {
    }

    internal EndpointRpcWindowsIpcRequestHandler(
        EndpointRpcDispatcher dispatcher,
        EndpointRpcMessageCodec messageCodec,
        EndpointRpcContractValidator validator,
        EndpointRpcSerializer serializer,
        EndpointLargeResultTransferStore largeResultTransferStore,
        EndpointRpcBackendErrorMapper errorMapper,
        IEndpointRpcAdmissionPolicy admissionPolicy)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _messageCodec = messageCodec ?? throw new ArgumentNullException(nameof(messageCodec));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _largeResultTransferStore = largeResultTransferStore
            ?? throw new ArgumentNullException(nameof(largeResultTransferStore));
        _errorMapper = errorMapper ?? throw new ArgumentNullException(nameof(errorMapper));
        _admissionPolicy = admissionPolicy
            ?? throw new ArgumentNullException(nameof(admissionPolicy));
        _largeResultValidator = new EndpointLargeResultContractValidator();
    }

    public IpcOperationId OperationId => IpcOperationId.EndpointRpcRequest;

    public async Task<IpcResponseEnvelope> HandleAsync(
        IpcRequestContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_admissionPolicy.TryEnterRequest(out var admissionLease))
        {
            return IpcResponseEnvelope.Failure(
                context.Request.CorrelationId,
                new IpcError(
                    IpcErrorCode.AgentUnavailable,
                    IpcErrorCategory.Availability,
                    "The Windows agent endpoint channel is unavailable.",
                    context.Request.CorrelationId,
                    DateTimeOffset.UtcNow,
                    IsRetryable: true,
                    RequiresProcessRestart: false));
        }

        using (admissionLease)
        {
            if (context.Request.Payload is null)
                return SuccessFailure(context, CreateInvalidPayloadError(context.Request.CorrelationId));

            byte[]? decodedPayload = null;
            try
            {
                EndpointRpcDecodedRequest decoded;
                try
                {
                    decoded = _messageCodec.DecodeRequest(context.Request.Payload);
                    decodedPayload = decoded.Payload;
                }
                catch (EndpointRpcPayloadException)
                {
                    return SuccessFailure(
                        context,
                        CreateInvalidPayloadError(context.Request.CorrelationId));
                }

                try
                {
                    return decoded.RequestKind switch
                    {
                        EndpointRpcRequestKind.PublicOperation => await HandlePublicOperationAsync(
                            context,
                            decoded.OperationId!.Value,
                            decodedPayload,
                            cancellationToken),
                        EndpointRpcRequestKind.GetLargeResultChunk => HandleLargeResultChunk(
                            context,
                            decodedPayload),
                        EndpointRpcRequestKind.ReleaseLargeResult => HandleLargeResultRelease(
                            context,
                            decodedPayload),
                        _ => SuccessFailure(
                            context,
                            CreateInvalidPayloadError(context.Request.CorrelationId))
                    };
                }
                catch (EndpointRpcPayloadException)
                    when (decoded.RequestKind != EndpointRpcRequestKind.PublicOperation)
                {
                    return SuccessFailure(
                        context,
                        CreateInvalidPayloadError(context.Request.CorrelationId));
                }
            }
            finally
            {
                EndpointSensitiveData.Clear(decodedPayload);
                EndpointSensitiveData.Clear(context.Request.Payload);
            }
        }
    }

    private async Task<IpcResponseEnvelope> HandlePublicOperationAsync(
        IpcRequestContext context,
        EndpointOperationId operationId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var endpointContext = new EndpointRequestContext(
            context.Connection.ConnectionId,
            context.Request.CorrelationId,
            operationId,
            context.Connection.PeerRole,
            context.Connection.PeerProcessId,
            context.Connection.PeerSessionId,
            cancellationToken);
        var result = await _dispatcher.DispatchAsync(
            endpointContext,
            payload,
            cancellationToken);

        byte[] encoded;
        if (!result.IsSuccess)
        {
            try
            {
                _validator.Validate(operationId, result.Error!);
                encoded = _messageCodec.EncodeFailure(result.Error!);
            }
            catch
            {
                var descriptor = EndpointOperationManifest.Get(operationId);
                if (!descriptor.MutatesState ||
                    endpointContext.Invocation.Stage < EndpointInvocationStage.Invoking)
                {
                    throw;
                }

                encoded = _messageCodec.EncodeFailure(
                    _errorMapper.CreateOutcomeUnknown(
                        endpointContext,
                        result.Error!.RequiresProcessRestart));
            }
        }
        else
        {
            try
            {
                if (result.IsLargeResult)
                {
                    try
                    {
                        encoded = _messageCodec.EncodeLargeResult(result.LargeResult!);
                    }
                    catch
                    {
                        _largeResultTransferStore.InvalidateIfOwned(
                            endpointContext.ConnectionId,
                            endpointContext.PeerSessionId,
                            result.LargeResult!.TransferId,
                            result.LargeResult.OriginalCorrelationId);
                        throw;
                    }
                }
                else
                {
                    try
                    {
                        encoded = _messageCodec.EncodeSuccess(result.Result!);
                    }
                    finally
                    {
                        EndpointSensitiveData.Clear(result.Result);
                    }
                }
            }
            catch
            {
                var descriptor = EndpointOperationManifest.Get(operationId);
                if (!descriptor.MutatesState ||
                    endpointContext.Invocation.Stage < EndpointInvocationStage.Invoking)
                {
                    throw;
                }

                encoded = _messageCodec.EncodeFailure(
                    _errorMapper.CreateOutcomeUnknown(endpointContext));
            }
        }

        return IpcResponseEnvelope.Success(context.Request.CorrelationId, encoded);
    }

    private IpcResponseEnvelope HandleLargeResultChunk(
        IpcRequestContext context,
        byte[] payload)
    {
        var request = _serializer.Deserialize(
            payload,
            EndpointRpcJsonContext.Default.GetEndpointLargeResultChunkRequest);
        try
        {
            _largeResultValidator.Validate(request);
        }
        catch
        {
            _largeResultTransferStore.InvalidateIfOwned(
                context.Connection.ConnectionId,
                context.Connection.PeerSessionId,
                request.TransferId,
                request.OriginalCorrelationId);
            throw;
        }

        var response = _largeResultTransferStore.GetChunk(
            context.Connection.ConnectionId,
            context.Connection.PeerSessionId,
            request);
        byte[]? serialized = null;
        try
        {
            _largeResultValidator.Validate(response);
            serialized = _serializer.Serialize(
                response,
                EndpointRpcJsonContext.Default.GetEndpointLargeResultChunkResponse);
            if (serialized.Length > EndpointRpcLimits.MaximumLargeResultChunkResponsePayloadSize)
            {
                throw new EndpointRpcPayloadException("The endpoint large-result chunk response is too large.");
            }

            return IpcResponseEnvelope.Success(
                context.Request.CorrelationId,
                _messageCodec.EncodeSuccess(serialized));
        }
        catch
        {
            _largeResultTransferStore.InvalidateIfOwned(
                context.Connection.ConnectionId,
                context.Connection.PeerSessionId,
                request.TransferId,
                request.OriginalCorrelationId);
            throw;
        }
        finally
        {
            EndpointSensitiveData.Clear(response.ChunkPayload);
            EndpointSensitiveData.Clear(serialized);
        }
    }

    private IpcResponseEnvelope HandleLargeResultRelease(
        IpcRequestContext context,
        byte[] payload)
    {
        var request = _serializer.Deserialize(
            payload,
            EndpointRpcJsonContext.Default.ReleaseEndpointLargeResultRequest);
        _largeResultValidator.Validate(request);
        var response = new ReleaseEndpointLargeResultResponse
        {
            Released = _largeResultTransferStore.Release(
                context.Connection.ConnectionId,
                context.Connection.PeerSessionId,
                request)
        };
        _largeResultValidator.Validate(response);
        var serialized = _serializer.Serialize(
            response,
            EndpointRpcJsonContext.Default.ReleaseEndpointLargeResultResponse);
        try
        {
            return IpcResponseEnvelope.Success(
                context.Request.CorrelationId,
                _messageCodec.EncodeSuccess(serialized));
        }
        finally
        {
            EndpointSensitiveData.Clear(serialized);
        }
    }

    private IpcResponseEnvelope SuccessFailure(
        IpcRequestContext context,
        EndpointRpcError error)
    {
        _validator.Validate(error);
        return IpcResponseEnvelope.Success(
            context.Request.CorrelationId,
            _messageCodec.EncodeFailure(error));
    }

    private static EndpointRpcError CreateInvalidPayloadError(long correlationId) =>
        new(
            EndpointRpcErrorCode.ValidationFailed,
            EndpointRpcErrorCategory.Validation,
            "The endpoint RPC payload is invalid.",
            correlationId,
            DateTimeOffset.UtcNow,
            IsRetryable: false,
            RequiresProcessRestart: false,
            EndpointMutationOutcome.NotApplicable,
            Recovery: null);
}
