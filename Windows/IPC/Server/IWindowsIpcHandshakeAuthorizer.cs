using PasswordManagerLocal.Windows.Ipc.Lifecycle;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public interface IWindowsIpcHandshakeAuthorizer
{
    IpcHandshakeAuthorizationDecision Authorize(IpcConnectionContext connection);
}
