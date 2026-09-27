using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

public interface IEndpointRpcAgentConnection : IAsyncDisposable
{
    bool IsConnected { get; }
    long ConnectionGeneration { get; }
    Task Completion { get; }

    Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<bool> PrepareForReplacementAsync(CancellationToken cancellationToken = default);
    Task<AgentStatusDto> GetAgentStatusAsync(CancellationToken cancellationToken = default);
    Task<BackendRuntimeStatusDto> GetBackendRuntimeStatusAsync(CancellationToken cancellationToken = default);
    Task<DatabaseResetResultDto> ResetDatabaseAsync(CancellationToken cancellationToken = default);
}
