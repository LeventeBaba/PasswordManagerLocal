namespace PasswordManagerLocal.Common.Contracts.Runtime;

public interface IBackendRuntimeClient : IAsyncDisposable
{
    BackendRuntimeSnapshot Snapshot { get; }

    event EventHandler<BackendRuntimeStateChangedEventArgs>? StateChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task WaitUntilReadyAsync(CancellationToken cancellationToken = default);
    Task ResetDatabaseAndRestartAsync(CancellationToken cancellationToken = default);
}
