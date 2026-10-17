namespace PasswordManagerLocal.Windows.Ipc.Server;

public interface IWindowsIpcChangeNotificationUserSelector
{
    Task<bool> SetUserFilterAsync(
        Guid connectionId,
        Guid? userId,
        bool enabled,
        CancellationToken cancellationToken = default);
}
