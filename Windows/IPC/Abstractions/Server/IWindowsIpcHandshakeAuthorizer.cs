using PasswordManagerLocal.Windows.Ipc.Lifecycle;

namespace PasswordManagerLocal.Windows.Ipc.Abstractions.Server;

public interface IWindowsIpcHandshakeAuthorizer
{
    IpcHandshakeAuthorizationDecision Authorize(IpcConnectionContext connection);
}
