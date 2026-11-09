using PasswordManagerLocal.Windows.Ipc.Transport;

namespace PasswordManagerLocal.Windows.Ipc.Abstractions.Server;

public interface IWindowsIpcServerSessionFactory
{
    IWindowsIpcServerSession Create(IWindowsIpcConnection connection);
}
