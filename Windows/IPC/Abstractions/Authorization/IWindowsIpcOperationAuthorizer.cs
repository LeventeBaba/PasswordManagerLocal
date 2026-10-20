using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Ipc.Abstractions.Authorization;

public interface IWindowsIpcOperationAuthorizer
{
    IpcAuthorizationDecision Authorize(IpcRequestContext context);
}
