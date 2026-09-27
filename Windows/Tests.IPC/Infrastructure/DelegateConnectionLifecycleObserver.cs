using PasswordManagerLocal.Windows.Ipc.Lifecycle;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class DelegateConnectionLifecycleObserver : IWindowsIpcConnectionLifecycleObserver
{
    private readonly Func<IpcConnectionLifecycleNotification, CancellationToken, ValueTask> _callback;

    public DelegateConnectionLifecycleObserver(
        Func<IpcConnectionLifecycleNotification, CancellationToken, ValueTask> callback)
    {
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    public ValueTask OnConnectionLifecycleChangedAsync(
        IpcConnectionLifecycleNotification notification,
        CancellationToken cancellationToken = default) =>
        _callback(notification, cancellationToken);
}
