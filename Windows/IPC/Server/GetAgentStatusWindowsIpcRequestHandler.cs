using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Validation;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public sealed class GetAgentStatusWindowsIpcRequestHandler : IWindowsIpcRequestHandler
{
    private readonly IWindowsIpcStatusProvider _statusProvider;
    private readonly WindowsIpcContractValidator _contractValidator;

    public GetAgentStatusWindowsIpcRequestHandler(
        IWindowsIpcStatusProvider statusProvider,
        WindowsIpcContractValidator? contractValidator = null)
    {
        _statusProvider = statusProvider ?? throw new ArgumentNullException(nameof(statusProvider));
        _contractValidator = contractValidator ?? new WindowsIpcContractValidator();
    }

    public IpcOperationId OperationId => IpcOperationId.GetAgentStatus;

    public async Task<IpcResponseEnvelope> HandleAsync(
        IpcRequestContext context,
        CancellationToken cancellationToken)
    {
        context.EnsureNoPayload();
        var status = await _statusProvider.GetAgentStatusAsync(cancellationToken);
        _contractValidator.Validate(status);
        return context.Success(status, WindowsIpcJsonContext.Default.AgentStatusDto);
    }
}
