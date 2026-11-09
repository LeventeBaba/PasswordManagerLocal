using PasswordManagerLocal.Windows.Ipc.Authorization;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Validation;

namespace PasswordManagerLocal.Windows.Ipc.Server.Dispatching;

public sealed class WindowsIpcRequestDispatcher
{
    private readonly IReadOnlyDictionary<Protocol.IpcOperationId, IWindowsIpcRequestHandler> _handlers;
    private readonly WindowsIpcContractValidator _contractValidator;
    private readonly IWindowsIpcOperationAuthorizer? _authorizer;

    public WindowsIpcRequestDispatcher(
        IEnumerable<IWindowsIpcRequestHandler> handlers,
        WindowsIpcContractValidator? contractValidator = null,
        IWindowsIpcOperationAuthorizer? authorizer = null)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        var registered = new Dictionary<Protocol.IpcOperationId, IWindowsIpcRequestHandler>();
        foreach (var handler in handlers)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (!Enum.IsDefined(handler.OperationId))
                throw new ArgumentException("An IPC handler has an unknown operation ID.", nameof(handlers));
            if (!registered.TryAdd(handler.OperationId, handler))
            {
                throw new InvalidOperationException(
                    $"An IPC handler is already registered for operation '{handler.OperationId}'.");
            }
        }

        _handlers = registered;
        _contractValidator = contractValidator ?? new WindowsIpcContractValidator();
        _authorizer = authorizer;
    }

    public async Task<IpcResponseEnvelope> DispatchAsync(
        IpcRequestContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_handlers.TryGetValue(context.Request.OperationId, out var handler))
        {
            return Failure(
                IpcErrorCode.UnknownOperation,
                IpcErrorCategory.Validation,
                "The requested IPC operation is not supported.",
                isRetryable: false);
        }

        if (_authorizer is not null)
        {
            var decision = _authorizer.Authorize(context);
            if (!decision.IsAuthorized)
            {
                return Failure(
                    decision.ErrorCode,
                    decision.ErrorCategory,
                    decision.SafeMessage,
                    decision.IsRetryable);
            }
        }

        try
        {
            var response = await handler.HandleAsync(context, cancellationToken);
            try
            {
                _contractValidator.Validate(response, context.CorrelationId);
            }
            catch (IpcPayloadLimitExceededException exception)
                when (exception.ErrorCode == IpcErrorCode.ResponsePayloadTooLarge)
            {
                return Failure(
                    exception.ErrorCode,
                    exception.ErrorCategory,
                    exception.SafeMessage,
                    exception.IsRetryable);
            }
            catch (IpcPayloadException)
            {
                return InvalidHandlerResponse();
            }

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                IpcErrorCode.RequestCancelled,
                IpcErrorCategory.Cancellation,
                "The IPC request was cancelled.",
                isRetryable: true);
        }
        catch (IpcPayloadLimitExceededException exception)
            when (exception.ErrorCode == IpcErrorCode.ResponsePayloadTooLarge)
        {
            return Failure(
                exception.ErrorCode,
                exception.ErrorCategory,
                exception.SafeMessage,
                exception.IsRetryable);
        }
        catch (IpcPayloadException)
        {
            return Failure(
                IpcErrorCode.InvalidPayload,
                IpcErrorCategory.Validation,
                "The IPC request payload is invalid.",
                isRetryable: false);
        }
        catch
        {
            return Failure(
                IpcErrorCode.HandlerFailed,
                IpcErrorCategory.Internal,
                "The IPC operation failed.",
                isRetryable: false);
        }
    }

    private static IpcResponseEnvelope InvalidHandlerResponse() =>
        Failure(
            IpcErrorCode.InvalidEnvelope,
            IpcErrorCategory.Internal,
            "The IPC handler returned an invalid response.",
            isRetryable: false);

    private static IpcResponseEnvelope Failure(
        IpcErrorCode code,
        IpcErrorCategory category,
        string safeMessage,
        bool isRetryable) =>
        IpcResponseEnvelope.Failure(
            new IpcError(
                code,
                category,
                safeMessage,
                DateTimeOffset.UtcNow,
                isRetryable,
                RequiresProcessRestart: false));
}
