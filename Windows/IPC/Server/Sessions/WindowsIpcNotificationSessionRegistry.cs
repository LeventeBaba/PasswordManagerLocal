using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Transport;

namespace PasswordManagerLocal.Windows.Ipc.Server.Sessions;

/// <summary>Routes metadata-only notifications to sessions explicitly registered as the UI.</summary>
public sealed class WindowsIpcNotificationSessionRegistry : IWindowsIpcNotificationSessionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, NotificationTarget> _sessions = [];

    public int RegisteredSessionCount
    {
        get { lock (_gate) return _sessions.Count; }
    }

    public bool HasTarget(Guid connectionId)
    {
        if (connectionId == Guid.Empty)
            return false;
        lock (_gate)
            return _sessions.ContainsKey(connectionId);
    }

    public bool IsEnabled(Guid connectionId)
    {
        if (connectionId == Guid.Empty)
            return false;
        lock (_gate)
            return _sessions.TryGetValue(connectionId, out var target) && target.Enabled;
    }

    public bool Enable(Guid connectionId)
    {
        if (connectionId == Guid.Empty)
            return false;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(connectionId, out var target))
                return false;
            target.Enabled = true;
            return true;
        }
    }

    public void Disable(Guid connectionId)
    {
        if (connectionId == Guid.Empty)
            return;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(connectionId, out var target))
                return;
            target.Enabled = false;
            target.Queue.ClearPending();
        }
    }

    public void ClearPending(Guid connectionId)
    {
        if (connectionId == Guid.Empty)
            return;
        lock (_gate)
        {
            if (_sessions.TryGetValue(connectionId, out var target))
                target.Queue.ClearPending();
        }
    }

    public bool TryRegister(Guid connectionId, IpcChangeNotificationQueue queue)
    {
        if (connectionId == Guid.Empty)
            throw new ArgumentException("A connection identity is required.", nameof(connectionId));
        ArgumentNullException.ThrowIfNull(queue);
        lock (_gate)
        {
            if (_sessions.TryGetValue(connectionId, out var existing))
                return ReferenceEquals(existing.Queue, queue);
            _sessions.Add(connectionId, new NotificationTarget(queue));
            return true;
        }
    }

    public void Unregister(Guid connectionId)
    {
        if (connectionId == Guid.Empty)
            return;
        lock (_gate)
            _sessions.Remove(connectionId);
    }

    public bool TryEnqueue(Guid connectionId, IpcChangeNotificationDto notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        lock (_gate)
            return _sessions.TryGetValue(connectionId, out var target) &&
                target.Enabled && target.Queue.TryEnqueue(notification);
    }

    private sealed class NotificationTarget(IpcChangeNotificationQueue queue)
    {
        public IpcChangeNotificationQueue Queue { get; } = queue;
        public bool Enabled { get; set; }
    }
}
