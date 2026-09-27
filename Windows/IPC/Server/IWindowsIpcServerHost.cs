namespace PasswordManagerLocal.Windows.Ipc.Server;

public interface IWindowsIpcServerHost : IAsyncDisposable
{
    int ActiveSessionCount { get; }
    Exception? ListenerFailure { get; }
    Task Completion { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task CloseActiveSessionsAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
