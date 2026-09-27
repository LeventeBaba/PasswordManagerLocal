using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Ipc.Validation;

namespace PasswordManagerLocal.Windows.Agent.Background;

public sealed class GetBackgroundSyncStateWindowsIpcRequestHandler : IWindowsIpcRequestHandler
{
    private readonly IWindowsBackgroundSyncCoordinator _coordinator;
    private readonly WindowsIpcContractValidator _validator;

    public GetBackgroundSyncStateWindowsIpcRequestHandler(
        IWindowsBackgroundSyncCoordinator coordinator,
        WindowsIpcContractValidator? validator = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _validator = validator ?? new WindowsIpcContractValidator();
    }

    public IpcOperationId OperationId => IpcOperationId.GetBackgroundSyncState;

    public async Task<IpcResponseEnvelope> HandleAsync(
        IpcRequestContext context,
        CancellationToken cancellationToken)
    {
        context.EnsureNoPayload();
        var state = await _coordinator.GetStateAsync(cancellationToken);
        _validator.Validate(state);
        return context.Success(state, WindowsIpcJsonContext.Default.WindowsBackgroundSyncStateDto);
    }
}
