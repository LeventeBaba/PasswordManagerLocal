namespace PasswordManagerLocal.Windows.Ipc.Abstractions.Server;

public interface IWindowsIpcChangeNotificationUserSelector
{
    Task<bool> SetUserFilterAsync(
        Guid connectionId,
        Guid? userId,
        bool enabled,
        CancellationToken cancellationToken = default);
}
