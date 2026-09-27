using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Ipc.Authorization;

public interface IWindowsIpcOperationAuthorizer
{
    IpcAuthorizationDecision Authorize(IpcRequestContext context);
}
