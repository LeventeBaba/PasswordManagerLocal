using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Agent.Preferences;

public sealed class ReloadApplicationPreferencesWindowsIpcRequestHandler : IWindowsIpcRequestHandler
{
    private readonly IAgentApplicationPreferencesReloadCoordinator _coordinator;

    public ReloadApplicationPreferencesWindowsIpcRequestHandler(
        IAgentApplicationPreferencesReloadCoordinator coordinator) =>
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public IpcOperationId OperationId => IpcOperationId.ReloadApplicationPreferences;

    public async Task<IpcResponseEnvelope> HandleAsync(
        IpcRequestContext context,
        CancellationToken cancellationToken)
    {
        context.EnsureNoPayload();
        var reloaded = await _coordinator.ReloadAsync(cancellationToken).ConfigureAwait(false);
        return context.Success(
            new RequestAcceptedDto(reloaded),
            WindowsIpcJsonContext.Default.RequestAcceptedDto);
    }
}
