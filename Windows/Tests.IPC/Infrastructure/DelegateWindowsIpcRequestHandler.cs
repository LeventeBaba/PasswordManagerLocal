using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class DelegateWindowsIpcRequestHandler : IWindowsIpcRequestHandler
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
