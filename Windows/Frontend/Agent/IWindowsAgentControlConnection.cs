using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Frontend.AgentConnection;

public interface IWindowsAgentControlConnection : IEndpointRpcAgentConnection
{
    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);
    Task<WindowsBackgroundSyncStateDto> GetBackgroundSyncStateAsync(
        CancellationToken cancellationToken = default);
    Task<WindowsBackgroundSyncStateDto> SetBackgroundSyncEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default);
    Task<bool> ReloadApplicationPreferencesAsync(CancellationToken cancellationToken = default);
}
