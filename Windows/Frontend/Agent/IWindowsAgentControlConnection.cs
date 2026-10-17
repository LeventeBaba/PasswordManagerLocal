using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Common.Contracts.Runtime;

namespace PasswordManagerLocal.Windows.Frontend.AgentConnection;

public interface IWindowsAgentControlConnection : IEndpointRpcAgentConnection, IFrontendChangeStreamClient
{
    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);
    Task<WindowsBackgroundSyncStateDto> GetBackgroundSyncStateAsync(
        CancellationToken cancellationToken = default);
    Task<WindowsBackgroundSyncStateDto> SetBackgroundSyncEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default);
    Task<bool> ReloadApplicationPreferencesAsync(CancellationToken cancellationToken = default);
}
