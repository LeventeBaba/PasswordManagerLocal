using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using System.Collections.Concurrent;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class RecordingLifecycleObserver : IWindowsIpcConnectionLifecycleObserver
{
    private readonly ConcurrentQueue<IpcConnectionLifecycleNotification> _notifications = new();

    public IReadOnlyCollection<IpcConnectionLifecycleNotification> Notifications =>
        _notifications.ToArray();

    public ValueTask OnConnectionLifecycleChangedAsync(
        IpcConnectionLifecycleNotification notification,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _notifications.Enqueue(notification);
        return ValueTask.CompletedTask;
    }
}
