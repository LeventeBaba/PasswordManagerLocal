using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Ipc.Transport;

namespace PasswordManagerLocal.Windows.EndpointRpc.Server;

public interface IEndpointRpcServerSessionFactory : IWindowsIpcServerSessionFactory, IAsyncDisposable
{
    new IEndpointRpcServerSession Create(IWindowsIpcConnection connection);
}
