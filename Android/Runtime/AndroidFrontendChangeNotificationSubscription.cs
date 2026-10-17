using System.Runtime.CompilerServices;
using PasswordManagerLocal.Common.Backend.Abstractions.Notifications;
using PasswordManagerLocal.Common.Contracts.Runtime;

namespace PasswordManagerLocal.Android.Runtime;

internal sealed class AndroidFrontendChangeNotificationSubscription :
    IFrontendChangeNotificationSubscription
{
    private readonly IBackendChangeSubscription _inner;
    private readonly Func<bool> _isCurrentAttachment;
    private readonly Action<AndroidFrontendChangeNotificationSubscription> _onUnsubscribed;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _unsubscribed;

    public AndroidFrontendChangeNotificationSubscription(
        IBackendChangeSubscription inner,
        Func<bool> isCurrentAttachment,
        Action<AndroidFrontendChangeNotificationSubscription> onUnsubscribed)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _isCurrentAttachment = isCurrentAttachment
            ?? throw new ArgumentNullException(nameof(isCurrentAttachment));
        _onUnsubscribed = onUnsubscribed
            ?? throw new ArgumentNullException(nameof(onUnsubscribed));
        Start = new FrontendChangeStreamStart(
            inner.Start.RuntimeInstanceId,
            inner.Start.Sequence,
            inner.Start.RequiresAuthoritativeRefresh);
    }

    public FrontendChangeStreamStart Start { get; }

    public async IAsyncEnumerable<FrontendChangeStreamDelivery> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _unsubscribed) != 0 || !_isCurrentAttachment())
            yield break;

        await foreach (var delivery in _inner.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Volatile.Read(ref _unsubscribed) != 0 || !_isCurrentAttachment())
                yield break;

            yield return new FrontendChangeStreamDelivery(
                delivery.RuntimeInstanceId,
                delivery.Sequence,
                delivery.Notification,
                delivery.RequiresAuthoritativeRefresh);
        }
    }

    public void Unsubscribe()
    {
        if (Interlocked.Exchange(ref _unsubscribed, 1) != 0)
            return;

        lock (_disposeGate)
        {
            try
            {
                _disposeTask = _inner.DisposeAsync().AsTask();
            }
            catch (Exception exception)
            {
                _disposeTask = Task.FromException(exception);
            }
        }
        _onUnsubscribed(this);
    }

    public async ValueTask DisposeAsync()
    {
        Unsubscribe();
        Task? disposeTask;
        lock (_disposeGate)
            disposeTask = _disposeTask;
        if (disposeTask is not null)
            await disposeTask.ConfigureAwait(false);
    }
}
