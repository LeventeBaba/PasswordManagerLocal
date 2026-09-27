using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public sealed class RegisterUiConnectionWindowsIpcRequestHandler : IWindowsIpcRequestHandler
{
    private readonly IUiConnectionCoordinator _coordinator;

    public RegisterUiConnectionWindowsIpcRequestHandler(IUiConnectionCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public IpcOperationId OperationId => IpcOperationId.RegisterUiConnection;

    public Task<IpcResponseEnvelope> HandleAsync(
        IpcRequestContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        context.EnsureNoPayload();

        if (context.Connection.PeerRole != IpcPeerRole.Ui)
        {
            return Task.FromResult(IpcResponseEnvelope.Failure(
                context.Request.CorrelationId,
                new IpcError(
                    IpcErrorCode.RequestRejected,
                    IpcErrorCategory.Validation,
                    "Only a UI connection can register as the interactive UI.",
                    context.Request.CorrelationId,
                    DateTimeOffset.UtcNow,
                    IsRetryable: false,
                    RequiresProcessRestart: false)));
        }

        if (!_coordinator.TryRegister(context.Connection, out _))
        {
            return Task.FromResult(IpcResponseEnvelope.Failure(
                context.Request.CorrelationId,
                new IpcError(
                    IpcErrorCode.UiAlreadyRegistered,
                    IpcErrorCategory.Conflict,
                    "Another UI connection is already registered.",
                    context.Request.CorrelationId,
                    DateTimeOffset.UtcNow,
                    IsRetryable: true,
                    RequiresProcessRestart: false)));
        }

        return Task.FromResult(context.Success(
            new UiConnectionRegistrationResponseDto(true),
            WindowsIpcJsonContext.Default.UiConnectionRegistrationResponseDto));
    }
}
