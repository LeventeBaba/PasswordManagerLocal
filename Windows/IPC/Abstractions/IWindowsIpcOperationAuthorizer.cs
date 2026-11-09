using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Ipc.Abstractions;

public interface IWindowsIpcOperationAuthorizer
{
    IpcAuthorizationDecision Authorize(IpcRequestContext context);
}
