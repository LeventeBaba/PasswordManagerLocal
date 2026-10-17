using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PasswordManagerLocal.Common.Contracts.Notifications;
using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Ipc.Transport;

/// <summary>
/// Bounded, non-blocking notification queue. Adjacent changes coalesce; overflow collapses
/// pending work to an authoritative-refresh marker at the latest known watermark.
/// </summary>
public sealed class IpcChangeNotificationQueue : IAsyncDisposable
{
    public const int DefaultCapacity = 32;

    private readonly object _gate = new();
    private readonly Queue<IpcChangeNotificationDto> _items = new();
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly int _capacity;
    private bool _completed;

    public IpcChangeNotificationQueue(int capacity = DefaultCapacity)
    {
        if (capacity <= 0 || capacity > 4096)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public bool TryEnqueue(IpcChangeNotificationDto notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        lock (_gate)
        {
            if (_completed)
                return false;

            if (_items.Count > 0)
            {
                var last = _items.Last();
                if (last.RuntimeInstanceId == notification.RuntimeInstanceId)
                {
                    var merged = Merge(last, notification);
                    ReplaceLast(merged);
                    Signal();
                    return true;
                }
            }

            if (_items.Count < _capacity)
            {
                _items.Enqueue(notification);
                Signal();
                return true;
            }

            var hasDifferentRuntime = _items.Any(item =>
                item.RuntimeInstanceId != notification.RuntimeInstanceId);
            var watermark = hasDifferentRuntime
                ? notification.Sequence
                : Math.Max(notification.Sequence, _items.Max(item => item.Sequence));
            _items.Clear();
            _items.Enqueue(new IpcChangeNotificationDto(
                notification.RuntimeInstanceId,
                watermark,
                FrontendChangeScope.Everything,
                notification.OccurredAtUtc,
                RequiresAuthoritativeRefresh: true));
            Signal();
            return true;
        }
    }

    public void ClearPending()
    {
        lock (_gate)
        {
            if (_completed)
                return;
            _items.Clear();
            Signal();
        }
    }

    public async ValueTask<IpcChangeNotificationDto?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            try
            {
                await _signal.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                lock (_gate)
                    return _items.Count == 0 ? null : _items.Dequeue();
            }
            lock (_gate)
            {
                if (_items.Count > 0)
                {
                    var item = _items.Dequeue();
                    if (_items.Count > 0)
                        Signal();
                    return item;
                }

                if (_completed)
                    return null;
            }
        }
    }

    public async IAsyncEnumerable<IpcChangeNotificationDto> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await ReadAsync(cancellationToken) is { } item)
            yield return item;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_completed)
                return ValueTask.CompletedTask;
            _completed = true;
            _items.Clear();
            Signal();
            _signal.Writer.TryComplete();
        }
        return ValueTask.CompletedTask;
    }

    private void ReplaceLast(IpcChangeNotificationDto value)
    {
        var values = _items.ToArray();
        _items.Clear();
        for (var index = 0; index < values.Length - 1; index++)
            _items.Enqueue(values[index]);
        _items.Enqueue(value);
    }

    private void Signal() => _signal.Writer.TryWrite(true);

    private static IpcChangeNotificationDto Merge(
        IpcChangeNotificationDto left,
        IpcChangeNotificationDto right) => new(
            right.RuntimeInstanceId,
            Math.Max(left.Sequence, right.Sequence),
            left.Scope | right.Scope,
            left.OccurredAtUtc >= right.OccurredAtUtc ? left.OccurredAtUtc : right.OccurredAtUtc,
            left.RequiresAuthoritativeRefresh || right.RequiresAuthoritativeRefresh);
}
