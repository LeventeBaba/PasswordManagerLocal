using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public interface IWindowsIpcStatusProvider
{
    Task<AgentStatusDto> GetAgentStatusAsync(CancellationToken cancellationToken);
    Task<BackendRuntimeStatusDto> GetBackendRuntimeStatusAsync(CancellationToken cancellationToken);
    Task<InteractiveSessionStatusDto> GetInteractiveSessionStatusAsync(CancellationToken cancellationToken);
    Task<SynchronizationStatusDto> GetSynchronizationStatusAsync(CancellationToken cancellationToken);
}
