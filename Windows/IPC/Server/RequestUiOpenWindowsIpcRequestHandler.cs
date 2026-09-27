using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public sealed class RequestUiOpenWindowsIpcRequestHandler : IWindowsIpcRequestHandler
{
    private readonly IUiOpenRequestSink _sink;

    public RequestUiOpenWindowsIpcRequestHandler(IUiOpenRequestSink sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public IpcOperationId OperationId => IpcOperationId.RequestUiOpen;

    public async Task<IpcResponseEnvelope> HandleAsync(
        IpcRequestContext context,
        CancellationToken cancellationToken)
    {
        var request = context.GetRequiredPayload(
            WindowsIpcJsonContext.Default.UiOpenRequestDto);
        var accepted = await _sink.RequestOpenAsync(request, cancellationToken);
        return context.Success(
            new RequestAcceptedDto(accepted),
            WindowsIpcJsonContext.Default.RequestAcceptedDto);
    }
}
