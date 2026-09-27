using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public sealed class PingWindowsIpcRequestHandler : IWindowsIpcRequestHandler
{
    public IpcOperationId OperationId => IpcOperationId.Ping;

    public Task<IpcResponseEnvelope> HandleAsync(
        IpcRequestContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        context.EnsureNoPayload();
        return Task.FromResult(context.Success(
            new PingResponseDto(DateTimeOffset.UtcNow),
            WindowsIpcJsonContext.Default.PingResponseDto));
    }
}
