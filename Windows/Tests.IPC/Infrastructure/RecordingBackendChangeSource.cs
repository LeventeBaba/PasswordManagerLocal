using System.Threading.Channels;
using PasswordManagerLocal.Common.Backend.Abstractions.Notifications;
using PasswordManagerLocal.Common.Contracts.Notifications;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal sealed class RecordingBackendChangeSource : IBackendChangeSource
{
    private readonly object _gate = new();
    private readonly HashSet<Subscription> _subscriptions = [];
    private BackendChangePosition _position = new(Guid.NewGuid(), 0);

    public BackendChangePosition CurrentPosition
    {
        get { lock (_gate) return _position; }
    }

    public int ActiveSubscriptionCount
    {
        get { lock (_gate) return _subscriptions.Count; }
    }

    public IReadOnlyList<Guid?> SubscribedUsers
    {
        get { lock (_gate) return _subscriptions.Select(item => item.UserId).ToArray(); }
    }

    public IBackendChangeSubscription Subscribe(Guid? userId = null, int capacity = 64)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("An empty user identity is invalid.", nameof(userId));
        if (capacity is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        lock (_gate)
        {
            var subscription = new Subscription(
                userId,
                new BackendChangeSubscriptionStart(
                    _position.RuntimeInstanceId,
                    _position.Sequence,
                    RequiresAuthoritativeRefresh: true),
                RemoveSubscription);
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    public BackendChangeDelivery Publish(
        FrontendChangeScope scope,
        Guid? userId = null,
        DateTimeOffset? occurredAtUtc = null)
    {
        lock (_gate)
        {
            var sequence = checked(_position.Sequence + 1);
            var notification = new FrontendChangeNotification(
                _position.RuntimeInstanceId,
                sequence,
                scope,
                userId,
                occurredAtUtc ?? DateTimeOffset.UtcNow);
            _position = new BackendChangePosition(notification.RuntimeInstanceId, notification.Sequence);
            var delivery = new BackendChangeDelivery(
                notification.RuntimeInstanceId,
                notification.Sequence,
                notification,
                RequiresAuthoritativeRefresh: false);
            foreach (var subscription in _subscriptions)
            {
                if (userId is null || subscription.UserId == userId)
                    subscription.Enqueue(delivery);
            }
            return delivery;
        }
    }

    public void RotateRuntime()
    {
        lock (_gate)
        {
            _position = new BackendChangePosition(Guid.NewGuid(), 0);
            var marker = new BackendChangeDelivery(
                _position.RuntimeInstanceId,
                _position.Sequence,
                Notification: null,
                RequiresAuthoritativeRefresh: true);
            foreach (var subscription in _subscriptions)
                subscription.Enqueue(marker);
        }
    }

    private void RemoveSubscription(Subscription subscription)
    {
        lock (_gate)
            _subscriptions.Remove(subscription);
    }

    private sealed class Subscription : IBackendChangeSubscription
    {
        private readonly Channel<BackendChangeDelivery> _channel = Channel.CreateUnbounded<BackendChangeDelivery>();
        private readonly Action<Subscription> _remove;
        private int _disposed;

        public Subscription(
            Guid? userId,
            BackendChangeSubscriptionStart start,
            Action<Subscription> remove)
        {
            UserId = userId;
            Start = start;
            _remove = remove;
        }

        public Guid? UserId { get; }
        public BackendChangeSubscriptionStart Start { get; }
        public IAsyncEnumerable<BackendChangeDelivery> ReadAllAsync(CancellationToken cancellationToken = default) =>
            _channel.Reader.ReadAllAsync(cancellationToken);

        public void Enqueue(BackendChangeDelivery delivery)
        {
            if (Volatile.Read(ref _disposed) == 0)
                _channel.Writer.TryWrite(delivery);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _remove(this);
                _channel.Writer.TryComplete();
            }
            return ValueTask.CompletedTask;
        }
    }
}
