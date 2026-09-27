using PasswordManagerLocal.Windows.Ipc.Transport;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public interface IWindowsIpcServerSessionFactory
{
    IWindowsIpcServerSession Create(IWindowsIpcConnection connection);
}
