using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Validation;

namespace PasswordManagerLocal.Windows.Ipc.Server.Handlers;

public sealed class SetChangeNotificationUserWindowsIpcRequestHandler : IWindowsIpcRequestHandler
{
    private readonly IWindowsIpcChangeNotificationUserSelector _userSelector;

    public SetChangeNotificationUserWindowsIpcRequestHandler(
        IWindowsIpcChangeNotificationUserSelector userSelector) =>
        _userSelector = userSelector ?? throw new ArgumentNullException(nameof(userSelector));

    public IpcOperationId OperationId => IpcOperationId.SetChangeNotificationUser;

    public async Task<IpcResponseEnvelope> HandleAsync(
        IpcRequestContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.GetRequiredPayload(
            WindowsIpcJsonContext.Default.SetChangeNotificationUserRequestDto);
        var selected = await _userSelector.SetUserFilterAsync(
            context.Connection.ConnectionId,
            request.UserId,
            request.Enabled,
            cancellationToken);
        if (!selected)
        {
            return IpcResponseEnvelope.Failure(
                new IpcError(
                    IpcErrorCode.UiNotRegistered,
                    IpcErrorCategory.Conflict,
                    "The change-notification filter requires the registered UI connection.",
                    DateTimeOffset.UtcNow,
                    IsRetryable: true,
                    RequiresProcessRestart: false));
        }

        return context.Success(new RequestAcceptedDto(true), WindowsIpcJsonContext.Default.RequestAcceptedDto);
    }
}
