using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Frontend.AgentConnection;

public interface IWindowsAgentRegisteredConnection : IAsyncDisposable
{
    bool IsConnected { get; }
    int? AgentProcessId { get; }
    Task Completion { get; }
    Task<AgentStatusDto> GetAgentStatusAsync(CancellationToken cancellationToken = default);
    Task<BackendRuntimeStatusDto> GetBackendRuntimeStatusAsync(CancellationToken cancellationToken = default);
    Task<WindowsBackgroundSyncStateDto> GetBackgroundSyncStateAsync(
        CancellationToken cancellationToken = default);
    Task<WindowsBackgroundSyncStateDto> SetBackgroundSyncEnabledAsync(
        SetBackgroundSyncEnabledRequestDto request,
        CancellationToken cancellationToken = default);
    Task<DatabaseResetResultDto> ResetDatabaseAsync(CancellationToken cancellationToken = default);
    Task<RequestAcceptedDto> ReloadApplicationPreferencesAsync(CancellationToken cancellationToken = default);
}
