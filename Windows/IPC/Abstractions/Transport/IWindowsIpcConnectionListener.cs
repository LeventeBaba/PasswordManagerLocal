namespace PasswordManagerLocal.Windows.Ipc.Abstractions.Transport;

public interface IWindowsIpcConnectionListener
{
    Task<IWindowsIpcConnection> AcceptAsync(CancellationToken cancellationToken = default);
}
