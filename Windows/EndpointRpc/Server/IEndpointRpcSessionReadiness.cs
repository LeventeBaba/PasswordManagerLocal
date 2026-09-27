using PasswordManagerLocal.Windows.Ipc.Lifecycle;

namespace PasswordManagerLocal.Windows.EndpointRpc.Server;

public interface IEndpointRpcSessionReadiness
{
    bool IsReady(IpcConnectionContext connection);
}
