namespace PasswordManagerLocal.Windows.Ipc.Abstractions.Lifecycle;

public interface IWindowsIpcConnectionLifecycleObserver
{
    ValueTask OnConnectionLifecycleChangedAsync(
        IpcConnectionLifecycleNotification notification,
        CancellationToken cancellationToken = default);
}
