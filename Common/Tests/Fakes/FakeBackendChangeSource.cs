using System.Threading.Channels;
using PasswordManagerLocal.Common.Backend.Abstractions.Notifications;
using PasswordManagerLocal.Common.Contracts.Notifications;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeBackendChangeSource : IBackendChangeSource, IBackendChangePublisher
{
    private readonly object _gate = new();
    private readonly HashSet<FakeSubscription> _subscriptions = [];
    private BackendChangePosition _position;

    public FakeBackendChangeSource(Guid? runtimeInstanceId = null)
    {
        _position = new BackendChangePosition(runtimeInstanceId ?? Guid.NewGuid(), 0);
    }

    public BackendChangePosition CurrentPosition
    {
        get { lock (_gate) return _position; }
    }

    public int ActiveSubscriptionCount
    {
        get { lock (_gate) return _subscriptions.Count; }
    }

    public int TotalSubscriptionCount { get; private set; }

    public IReadOnlyList<Guid?> SubscribedUsers
    {
        get { lock (_gate) return _subscriptions.Select(subscription => subscription.UserId).ToArray(); }
    }

    public IBackendChangeSubscription Subscribe(Guid? userId = null, int capacity = 64)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("An empty user identity is invalid.", nameof(userId));
        if (capacity is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        lock (_gate)
        {
            var subscription = new FakeSubscription(
                userId,
                capacity,
                new BackendChangeSubscriptionStart(
                    _position.RuntimeInstanceId,
                    _position.Sequence,
                    RequiresAuthoritativeRefresh: true),
                RemoveSubscription);
            _subscriptions.Add(subscription);
            TotalSubscriptionCount++;
            return subscription;
        }
    }

    public FrontendChangeNotification? Publish(FrontendChangeScope scope, Guid? userId = null)
    {
        lock (_gate)
        {
            var notification = new FrontendChangeNotification(
                _position.RuntimeInstanceId,
                checked(_position.Sequence + 1),
                scope,
                userId,
                DateTimeOffset.UnixEpoch.AddTicks(_position.Sequence + 1));
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

            return notification;
        }
    }

    public void ReplaceRuntime(Guid? runtimeInstanceId = null)
    {
        lock (_gate)
        {
            _position = new BackendChangePosition(runtimeInstanceId ?? Guid.NewGuid(), 0);
            foreach (var subscription in _subscriptions.ToArray())
                subscription.Complete();
            _subscriptions.Clear();
        }
    }

    private void RemoveSubscription(FakeSubscription subscription)
    {
        lock (_gate)
            _subscriptions.Remove(subscription);
    }

    private sealed class FakeSubscription : IBackendChangeSubscription
    {
        private readonly Channel<BackendChangeDelivery> _channel;
        private readonly Action<FakeSubscription> _remove;
        private readonly object _writerGate = new();
        private int _disposed;

        public FakeSubscription(
            Guid? userId,
            int capacity,
            BackendChangeSubscriptionStart start,
            Action<FakeSubscription> remove)
        {
            UserId = userId;
            Start = start;
            _remove = remove;
            _channel = Channel.CreateBounded<BackendChangeDelivery>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }

        public Guid? UserId { get; }
        public BackendChangeSubscriptionStart Start { get; }
        public IAsyncEnumerable<BackendChangeDelivery> ReadAllAsync(CancellationToken cancellationToken = default) =>
            _channel.Reader.ReadAllAsync(cancellationToken);

        public void Enqueue(BackendChangeDelivery delivery)
        {
            lock (_writerGate)
            {
                if (Volatile.Read(ref _disposed) != 0 || _channel.Writer.TryWrite(delivery))
                    return;

                while (_channel.Reader.TryRead(out _))
                {
                }

                _channel.Writer.TryWrite(new BackendChangeDelivery(
                    delivery.RuntimeInstanceId,
                    delivery.Sequence,
                    Notification: null,
                    RequiresAuthoritativeRefresh: true));
            }
        }

        public ValueTask DisposeAsync()
        {
            Complete();
            return ValueTask.CompletedTask;
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _remove(this);
            _channel.Writer.TryComplete();
        }
    }
}
