using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public sealed class RequestUiActivationWindowsIpcRequestHandler : IWindowsIpcRequestHandler
{
    private readonly IUiActivationRequestSink _sink;

    public RequestUiActivationWindowsIpcRequestHandler(IUiActivationRequestSink sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public IpcOperationId OperationId => IpcOperationId.RequestUiActivation;

    public async Task<IpcResponseEnvelope> HandleAsync(
        IpcRequestContext context,
        CancellationToken cancellationToken)
    {
        var request = context.GetRequiredPayload(
            WindowsIpcJsonContext.Default.UiActivationRequestDto);
        var accepted = await _sink.RequestActivationAsync(request, cancellationToken);
        return context.Success(
            new RequestAcceptedDto(accepted),
            WindowsIpcJsonContext.Default.RequestAcceptedDto);
    }
}
