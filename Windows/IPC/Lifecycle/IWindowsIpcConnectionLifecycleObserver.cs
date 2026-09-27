namespace PasswordManagerLocal.Windows.Ipc.Lifecycle;

public interface IWindowsIpcConnectionLifecycleObserver
{
    ValueTask OnConnectionLifecycleChangedAsync(
        IpcConnectionLifecycleNotification notification,
        CancellationToken cancellationToken = default);
}
