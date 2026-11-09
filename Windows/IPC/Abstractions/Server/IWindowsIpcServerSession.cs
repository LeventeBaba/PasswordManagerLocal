namespace PasswordManagerLocal.Windows.Ipc.Abstractions.Server;

public interface IWindowsIpcServerSession : IAsyncDisposable
{
    Task RunAsync(CancellationToken cancellationToken = default);
}
