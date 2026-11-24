namespace PasswordManagerLocal.Common.Contracts.Runtime;

public interface IBackendRuntimeClient : IAsyncDisposable
{
    BackendRuntimeSnapshot Snapshot { get; }

    event EventHandler<BackendRuntimeStateChangedEventArgs>? StateChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task WaitUntilReadyAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Explicitly resets eligible failed-startup storage. Cancellation may abort
    /// before destructive work; after that boundary the caller observes the outcome.
    /// </summary>
    Task ResetDatabaseAndRestartAsync(CancellationToken cancellationToken = default);
}
