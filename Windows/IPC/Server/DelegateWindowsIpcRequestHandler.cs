using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public sealed class DelegateWindowsIpcRequestHandler : IWindowsIpcRequestHandler
{
    private readonly Func<IpcRequestContext, CancellationToken, Task<IpcResponseEnvelope>> _handler;

    public DelegateWindowsIpcRequestHandler(
        IpcOperationId operationId,
        Func<IpcRequestContext, CancellationToken, Task<IpcResponseEnvelope>> handler)
    {
        OperationId = operationId;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public IpcOperationId OperationId { get; }

    public Task<IpcResponseEnvelope> HandleAsync(
        IpcRequestContext context,
        CancellationToken cancellationToken) =>
        _handler(context, cancellationToken);
}
