using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Agent.DatabaseReset;

public sealed class ResetDatabaseWindowsIpcRequestHandler : IWindowsIpcRequestHandler
{
    private readonly IWindowsAgentDatabaseResetCoordinator _coordinator;

    public ResetDatabaseWindowsIpcRequestHandler(
        IWindowsAgentDatabaseResetCoordinator coordinator) =>
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public IpcOperationId OperationId => IpcOperationId.ResetDatabase;

    public async Task<IpcResponseEnvelope> HandleAsync(
        IpcRequestContext context,
        CancellationToken cancellationToken)
    {
        context.EnsureNoPayload();
        var result = await _coordinator.ResetAsync(cancellationToken);
        return context.Success(result, WindowsIpcJsonContext.Default.DatabaseResetResultDto);
    }
}
