using PasswordManagerLocal.Common.Backend.Hosting;
using PasswordManagerLocal.Common.Backend.Abstractions.Notifications;

namespace PasswordManagerLocal.Windows.Agent.Backend;

public interface IWindowsAgentBackendRuntimeOwner : IAsyncDisposable
{
    WindowsAgentBackendOwnerSnapshot Snapshot { get; }
    IBackendChangeSource? ChangeSource { get; }
    event EventHandler? StateChanged;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task<IBackendRuntimeLease> AcquireBackgroundSyncLeaseAsync(
        CancellationToken cancellationToken = default);
    Task<AgentInteractiveBackendBinding> OpenInteractiveBindingAsync(
        CancellationToken cancellationToken = default);
    Task ResetDatabaseAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    void RequireProcessRestart(Exception failure);
}
