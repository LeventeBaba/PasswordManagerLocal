namespace PasswordManagerLocal.Windows.Ipc.Server;

public interface IWindowsIpcServerSession : IAsyncDisposable
{
    Task RunAsync(CancellationToken cancellationToken = default);
}
