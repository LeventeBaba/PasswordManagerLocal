using PasswordManagerLocal.Common.Contracts.Runtime;
using PasswordManagerLocal.Common.Contracts.BackgroundSync;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Hosting;

public interface IBackendRuntime : IAsyncDisposable
{
    BackendRuntimeSnapshot Snapshot { get; }
    InteractiveSessionLifecycleSnapshot InteractiveSessionSnapshot { get; }
    SyncRuntimeSnapshot SyncSnapshot { get; }

    event EventHandler<BackendRuntimeStateChangedEventArgs>? StateChanged;
    event EventHandler<SyncRuntimeStateChangedEventArgs>? SyncStateChanged;

    Task EnsureStartedAsync(CancellationToken cancellationToken = default);
    Task WaitUntilReadyAsync(CancellationToken cancellationToken = default);
    Task<IInteractiveBackendSession> OpenInteractiveSessionAsync(
        CancellationToken cancellationToken = default);
    Task ResetDatabaseAndRestartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
