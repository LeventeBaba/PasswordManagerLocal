namespace PasswordManagerLocal.Windows.Ipc.Transport;

public interface IWindowsIpcConnectionListener
{
    Task<IWindowsIpcConnection> AcceptAsync(CancellationToken cancellationToken = default);
}
