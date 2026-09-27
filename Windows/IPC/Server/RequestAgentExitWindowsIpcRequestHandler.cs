using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public sealed class RequestAgentExitWindowsIpcRequestHandler : IWindowsIpcRequestHandler
{
    private readonly IAgentExitRequestSink _sink;

    public RequestAgentExitWindowsIpcRequestHandler(IAgentExitRequestSink sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public IpcOperationId OperationId => IpcOperationId.RequestAgentExit;

    public async Task<IpcResponseEnvelope> HandleAsync(
        IpcRequestContext context,
        CancellationToken cancellationToken)
    {
        var request = context.GetRequiredPayload(
            WindowsIpcJsonContext.Default.AgentExitRequestDto);
        var accepted = await _sink.RequestExitAsync(request, cancellationToken);
        return context.Success(
            new RequestAcceptedDto(accepted),
            WindowsIpcJsonContext.Default.RequestAcceptedDto);
    }
}
