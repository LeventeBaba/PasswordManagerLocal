using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public sealed class UnregisterUiConnectionWindowsIpcRequestHandler : IWindowsIpcRequestHandler
{
    private readonly IUiConnectionCoordinator _coordinator;

    public UnregisterUiConnectionWindowsIpcRequestHandler(IUiConnectionCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public IpcOperationId OperationId => IpcOperationId.UnregisterUiConnection;

    public Task<IpcResponseEnvelope> HandleAsync(
        IpcRequestContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        context.EnsureNoPayload();
        if (!_coordinator.Unregister(context.Connection.ConnectionId))
        {
            return Task.FromResult(IpcResponseEnvelope.Failure(
                context.Request.CorrelationId,
                new IpcError(
                    IpcErrorCode.UiNotRegistered,
                    IpcErrorCategory.Conflict,
                    "The connection is not the registered UI.",
                    context.Request.CorrelationId,
                    DateTimeOffset.UtcNow,
                    IsRetryable: true,
                    RequiresProcessRestart: false)));
        }

        return Task.FromResult(context.Success(
            new UiConnectionRegistrationResponseDto(false),
            WindowsIpcJsonContext.Default.UiConnectionRegistrationResponseDto));
    }
}
