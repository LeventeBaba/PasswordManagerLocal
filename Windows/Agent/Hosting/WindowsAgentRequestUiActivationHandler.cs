using PasswordManagerLocal.Windows.Agent.Ui;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Agent.Hosting;

public sealed class WindowsAgentRequestUiActivationHandler : IWindowsIpcRequestHandler
{
    private readonly IWindowsUiOpenService _openService;

    public WindowsAgentRequestUiActivationHandler(IWindowsUiOpenService openService)
    {
        _openService = openService ?? throw new ArgumentNullException(nameof(openService));
    }

    public IpcOperationId OperationId => IpcOperationId.RequestUiActivation;

    public async Task<IpcResponseEnvelope> HandleAsync(
        IpcRequestContext context,
        CancellationToken cancellationToken)
    {
        var request = context.GetRequiredPayload(WindowsIpcJsonContext.Default.UiActivationRequestDto);
        var result = await _openService.OpenAsync(request.Reason, cancellationToken);
        if (result.IsSuccess)
        {
            return context.Success(
                new RequestAcceptedDto(true),
                WindowsIpcJsonContext.Default.RequestAcceptedDto);
        }

        var (code, category, retryable) = result.Kind switch
        {
            UiOpenResultKind.ExecutableNotFound =>
                (IpcErrorCode.UiExecutableNotFound, IpcErrorCategory.Availability, false),
            UiOpenResultKind.LaunchFailed =>
                (IpcErrorCode.UiLaunchFailed, IpcErrorCategory.Availability, true),
            UiOpenResultKind.ActivationRejected =>
                (IpcErrorCode.UiActivationRejected, IpcErrorCategory.Conflict, false),
            UiOpenResultKind.ActivationFailed =>
                (IpcErrorCode.UiActivationFailed, IpcErrorCategory.Internal, false),
            _ =>
                (IpcErrorCode.UiActivationUnavailable, IpcErrorCategory.Availability, true)
        };
        return IpcResponseEnvelope.Failure(
            context.Request.CorrelationId,
            new IpcError(
                code,
                category,
                result.SafeMessage,
                context.Request.CorrelationId,
                DateTimeOffset.UtcNow,
                retryable,
                RequiresProcessRestart: false));
    }
}
