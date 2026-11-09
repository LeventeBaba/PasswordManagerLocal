using System.Threading.Channels;
using PasswordManagerLocal.Common.Backend.Abstractions.Notifications;
using PasswordManagerLocal.Common.Contracts.Notifications;

namespace PasswordManagerLocal.Common.Backend.Sync;

/// <summary>
/// One event hub belongs to one backend service provider. It performs no subscriber callbacks;
/// each subscriber receives changes through its own bounded asynchronous channel.
/// </summary>
public sealed class BackendChangeEventHub : IBackendChangePublisher, IBackendChangeSource, IDisposable
{
    public const int DefaultSubscriberCapacity = 64;
    public const int MaximumSubscriberCapacity = 256;

    private readonly object _gate = new();
    private readonly HashSet<BackendChangeSubscription> _subscriptions = [];
    private readonly TimeProvider _timeProvider;
    private Guid _runtimeInstanceId;
    private long _sequence;
    private bool _disposed;

    public BackendChangeEventHub(TimeProvider timeProvider)
        : this(timeProvider, Guid.NewGuid(), 0)
    {
    }

    internal BackendChangeEventHub(TimeProvider timeProvider, Guid runtimeInstanceId, long sequence)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (runtimeInstanceId == Guid.Empty)
            throw new ArgumentException("A runtime identity is required.", nameof(runtimeInstanceId));
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));

        _runtimeInstanceId = runtimeInstanceId;
        _sequence = sequence;
    }

    public BackendChangePosition CurrentPosition
    {
        get
        {
            lock (_gate)
                return new BackendChangePosition(_runtimeInstanceId, _sequence);
        }
    }

    public FrontendChangeNotification? Publish(FrontendChangeScope scope, Guid? userId = null)
    {
        ValidatePublication(scope, userId);

        lock (_gate)
        {
            if (_disposed)
                return null;

            if (_sequence == long.MaxValue)
            {
                _runtimeInstanceId = CreateRuntimeInstanceId();
                _sequence = 0;
                foreach (var subscription in _subscriptions)
                    SafeEnqueueRuntimeReplacement(subscription, _runtimeInstanceId);
            }

            var notification = new FrontendChangeNotification(
                _runtimeInstanceId,
                ++_sequence,
                scope,
                userId,
                _timeProvider.GetUtcNow());
            var delivery = new BackendChangeDelivery(
                notification.RuntimeInstanceId,
                notification.Sequence,
                notification,
                RequiresAuthoritativeRefresh: false);

            foreach (var subscription in _subscriptions)
                SafeEnqueue(subscription, delivery);

            return notification;
        }
    }

    public IBackendChangeSubscription Subscribe(Guid? userId = null, int capacity = DefaultSubscriberCapacity)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("An empty user identity is invalid.", nameof(userId));
        if (capacity is < 1 or > MaximumSubscriberCapacity)
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                $"Subscriber capacity must be between 1 and {MaximumSubscriberCapacity}.");

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var start = new BackendChangeSubscriptionStart(
                _runtimeInstanceId,
                _sequence,
                RequiresAuthoritativeRefresh: true);
            var subscription = new BackendChangeSubscription(userId, capacity, start, RemoveSubscription);
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    public void Dispose()
    {
        BackendChangeSubscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
            subscription.Complete();
    }

    private void RemoveSubscription(BackendChangeSubscription subscription)
    {
        lock (_gate)
            _subscriptions.Remove(subscription);
    }

    private static void SafeEnqueue(
        BackendChangeSubscription subscription,
        BackendChangeDelivery delivery)
    {
        try
        {
            subscription.Enqueue(delivery);
        }
        catch
        {
            // A broken or concurrently disposed observer must not fail a committed mutation.
        }
    }

    private static void SafeEnqueueRuntimeReplacement(
        BackendChangeSubscription subscription,
        Guid runtimeInstanceId)
    {
        try
        {
            subscription.EnqueueRuntimeReplacement(runtimeInstanceId);
        }
        catch
        {
            // A broken or concurrently disposed observer must not fail a committed mutation.
        }
    }

    private static Guid CreateRuntimeInstanceId()
    {
        Guid value;
        do
        {
            value = Guid.NewGuid();
        }
        while (value == Guid.Empty);

        return value;
    }

    private static void ValidatePublication(FrontendChangeScope scope, Guid? userId)
    {
        if (scope == FrontendChangeScope.None || (scope & ~FrontendChangeScope.Everything) != 0)
            throw new ArgumentOutOfRangeException(nameof(scope), "A known, non-empty change scope is required.");
        if (userId == Guid.Empty)
            throw new ArgumentException("An empty user identity is invalid.", nameof(userId));
    }

    private sealed class BackendChangeSubscription : IBackendChangeSubscription
    {
        private readonly Guid? _userId;
        private readonly Channel<BackendChangeDelivery> _channel;
        private readonly Action<BackendChangeSubscription> _onDispose;
        private readonly object _writerGate = new();
        private int _disposed;

        public BackendChangeSubscription(
            Guid? userId,
            int capacity,
            BackendChangeSubscriptionStart start,
            Action<BackendChangeSubscription> onDispose)
        {
            _userId = userId;
            Start = start;
            _onDispose = onDispose;
            _channel = Channel.CreateBounded<BackendChangeDelivery>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }

        public BackendChangeSubscriptionStart Start { get; }

        public IAsyncEnumerable<BackendChangeDelivery> ReadAllAsync(CancellationToken cancellationToken = default) =>
            _channel.Reader.ReadAllAsync(cancellationToken);

        public void Enqueue(BackendChangeDelivery delivery)
        {
            if (delivery.Notification is { UserId: not null } notification &&
                _userId != notification.UserId)
            {
                return;
            }

            lock (_writerGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return;
                if (_channel.Writer.TryWrite(delivery))
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

        public void EnqueueRuntimeReplacement(Guid runtimeInstanceId)
        {
            lock (_writerGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return;

                var marker = new BackendChangeDelivery(
                    runtimeInstanceId,
                    Sequence: 0,
                    Notification: null,
                    RequiresAuthoritativeRefresh: true);
                if (_channel.Writer.TryWrite(marker))
                    return;

                while (_channel.Reader.TryRead(out _))
                {
                }

                _channel.Writer.TryWrite(marker);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _onDispose(this);
            Complete();
        }

        public void Complete()
        {
            lock (_writerGate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    _onDispose(this);
                _channel.Writer.TryComplete();
            }
        }
    }
}
