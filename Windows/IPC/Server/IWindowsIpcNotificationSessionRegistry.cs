using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Transport;

namespace PasswordManagerLocal.Windows.Ipc.Server;

public interface IWindowsIpcNotificationSessionRegistry
{
    bool HasTarget(Guid connectionId);
    bool IsEnabled(Guid connectionId);
    bool Enable(Guid connectionId);
    void Disable(Guid connectionId);
    void ClearPending(Guid connectionId);
    bool TryRegister(Guid connectionId, IpcChangeNotificationQueue queue);
    void Unregister(Guid connectionId);
    bool TryEnqueue(Guid connectionId, IpcChangeNotificationDto notification);
}
