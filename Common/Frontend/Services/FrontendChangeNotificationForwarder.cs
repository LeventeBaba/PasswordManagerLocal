using PasswordManagerLocal.Common.Contracts.Runtime;

namespace PasswordManagerLocal.Common.Frontend.Services;

/// <summary>
/// Forwards bounded backend change hints to one live frontend session. It owns no replay queue;
/// every replacement subscription begins at the source's atomic watermark.
/// </summary>
public sealed class FrontendChangeNotificationForwarder : IAsyncDisposable
{
    private const int SubscriptionCapacity = 64;

    private readonly IBackendRuntimeClient _runtimeClient;
    private readonly IFrontendChangeNotificationClient? _notificationClient;
    private readonly Func<Guid?> _userIdProvider;
    private readonly Func<bool> _isSessionActive;
    private readonly CancellationToken _sessionLifetime;
    private readonly object _gate = new();
    private IFrontendChangeNotificationSubscription? _subscription;
    private CancellationTokenSource? _pumpCancellation;
    private Task _pumpTask = Task.CompletedTask;
    private Task _retirementTask = Task.CompletedTask;
    private Guid? _subscribedUserId;
    private FrontendChangeStreamStart? _currentPosition;
    private long _generation;
    private int _disposed;

    public FrontendChangeNotificationForwarder(
        IBackendRuntimeClient runtimeClient,
        IFrontendChangeNotificationClient? notificationClient,
        Func<Guid?> userIdProvider,
        Func<bool> isSessionActive,
        CancellationToken sessionLifetime)
    {
        _runtimeClient = runtimeClient ?? throw new ArgumentNullException(nameof(runtimeClient));
        _notificationClient = notificationClient;
        _userIdProvider = userIdProvider ?? throw new ArgumentNullException(nameof(userIdProvider));
        _isSessionActive = isSessionActive ?? throw new ArgumentNullException(nameof(isSessionActive));
        _sessionLifetime = sessionLifetime;

        _runtimeClient.StateChanged += HandleRuntimeStateChanged;
        if (_notificationClient is not null)
            _notificationClient.ChangeSourceChanged += HandleChangeSourceChanged;

        if (_runtimeClient.Snapshot.IsReady)
            Reconcile(forceRestart: true);
    }

    /// <summary>Raised with metadata-only hints while this session remains active.</summary>
    public event Action<FrontendChangeStreamDelivery>? ChangeHintReceived;

    /// <summary>The latest runtime watermark observed by this session's current stream.</summary>
    public FrontendChangeStreamStart? CurrentPosition
    {
        get
        {
            lock (_gate)
                return _currentPosition;
        }
    }

    /// <summary>Registers at a fresh watermark, for example when an Activity resumes.</summary>
    public void ReconcileOnResume() => Reconcile(forceRestart: true);

    /// <summary>Starts the current user scope if startup did not already do so.</summary>
    public void ReconcileCurrentScope() => Reconcile(forceRestart: false);

    /// <summary>Refreshes the subscription after the authenticated user changes.</summary>
    public void ReconcileUserScope() => Reconcile(forceRestart: false);

    /// <summary>
    /// Revokes the stream and callback admission synchronously. DisposeAsync can then drain the
    /// reader without blocking an Activity lifecycle callback.
    /// </summary>
    public void Deactivate()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _runtimeClient.StateChanged -= HandleRuntimeStateChanged;
            if (_notificationClient is not null)
                _notificationClient.ChangeSourceChanged -= HandleChangeSourceChanged;
            RetireCurrentLocked(waitForPump: true);
            ChangeHintReceived = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Deactivate();
        Task retirement;
        lock (_gate)
            retirement = _retirementTask;
        await retirement.ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void HandleRuntimeStateChanged(
        object? sender,
        BackendRuntimeStateChangedEventArgs args) =>
        Reconcile(forceRestart: false);

    private void HandleChangeSourceChanged(object? sender, EventArgs args) =>
        Reconcile(forceRestart: true);

    private void Reconcile(bool forceRestart)
    {
        if (_notificationClient is null)
            return;

        IFrontendChangeNotificationSubscription? subscription = null;
        CancellationTokenSource? pumpCancellation = null;
        FrontendChangeStreamDelivery? startHint = null;
        long generation = 0;

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            if (!IsSessionActiveNoThrow() || !IsRuntimeReadyNoThrow())
            {
                RetireCurrentLocked(waitForPump: true);
                return;
            }

            Guid? userId;
            try
            {
                userId = _userIdProvider();
                if (userId == Guid.Empty)
                    userId = null;
            }
            catch
            {
                userId = null;
            }

            if (!forceRestart && _subscription is not null && _subscribedUserId == userId)
                return;

            RetireCurrentLocked(waitForPump: true);
            try
            {
                subscription = _notificationClient.SubscribeChangeNotifications(
                    userId,
                    SubscriptionCapacity);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                // Attachment authority or readiness changed during the handoff. The next
                // runtime/source lifecycle signal or Activity resume will retry from a new watermark.
                return;
            }

            if (!IsSessionActiveNoThrow() || !IsRuntimeReadyNoThrow())
            {
                SafeUnsubscribe(subscription);
                QueueRetirementLocked(subscription, Task.CompletedTask, null);
                return;
            }

            generation = ++_generation;
            pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(_sessionLifetime);
            _subscription = subscription;
            _pumpCancellation = pumpCancellation;
            _pumpTask = Task.CompletedTask;
            _subscribedUserId = userId;
            _currentPosition = subscription.Start;
            if (subscription.Start.RequiresAuthoritativeRefresh)
            {
                startHint = new FrontendChangeStreamDelivery(
                    subscription.Start.RuntimeInstanceId,
                    subscription.Start.Sequence,
                    Notification: null,
                    RequiresAuthoritativeRefresh: true);
            }
        }

        if (startHint is not null)
            PublishIfCurrent(generation, subscription!, pumpCancellation!, startHint);

        lock (_gate)
        {
            if (IsCurrentLocked(generation, subscription!, pumpCancellation!))
            {
                _pumpTask = Task.Run(() => PumpAsync(
                    generation,
                    subscription!,
                    pumpCancellation!));
            }
        }
    }

    private async Task PumpAsync(
        long generation,
        IFrontendChangeNotificationSubscription subscription,
        CancellationTokenSource pumpCancellation)
    {
        try
        {
            await foreach (var delivery in subscription
                .ReadAllAsync(pumpCancellation.Token)
                .ConfigureAwait(false))
            {
                var forwarded = delivery;
                lock (_gate)
                {
                    if (!IsCurrentLocked(generation, subscription, pumpCancellation))
                        return;

                    if (_currentPosition is { } previous &&
                        previous.RuntimeInstanceId != delivery.RuntimeInstanceId)
                    {
                        // Runtime identity changes always require authoritative state. Keep
                        // the delivered runtime watermark; never relabel it as the old runtime.
                        forwarded = new FrontendChangeStreamDelivery(
                            delivery.RuntimeInstanceId,
                            delivery.Sequence,
                            Notification: null,
                            RequiresAuthoritativeRefresh: true);
                    }

                    _currentPosition = new FrontendChangeStreamStart(
                        delivery.RuntimeInstanceId,
                        delivery.Sequence,
                        delivery.RequiresAuthoritativeRefresh);
                    PublishLocked(forwarded, generation, subscription, pumpCancellation);
                }
            }
        }
        catch (OperationCanceledException) when (pumpCancellation.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            // Stream failures carry no user data to the UI. The next lifecycle/reconnect
            // signal starts a fresh subscription and requests authoritative reconciliation.
        }

        HandleStreamEnded(generation, subscription, pumpCancellation);
    }

    private void HandleStreamEnded(
        long generation,
        IFrontendChangeNotificationSubscription subscription,
        CancellationTokenSource pumpCancellation)
    {
        lock (_gate)
        {
            if (!IsCurrentLocked(generation, subscription, pumpCancellation))
                return;
            // A stream ending can mean its attachment became obsolete. Do not emit a hint
            // from that retired attachment; the next ready/reconnect/resume registration
            // supplies a fresh runtime watermark and authoritative-refresh marker.
            RetireCurrentLocked(waitForPump: false);
        }
    }

    private void PublishIfCurrent(
        long generation,
        IFrontendChangeNotificationSubscription subscription,
        CancellationTokenSource pumpCancellation,
        FrontendChangeStreamDelivery delivery)
    {
        lock (_gate)
        {
            if (IsCurrentLocked(generation, subscription, pumpCancellation))
                PublishLocked(delivery, generation, subscription, pumpCancellation);
        }
    }

    private void PublishLocked(
        FrontendChangeStreamDelivery delivery,
        long generation,
        IFrontendChangeNotificationSubscription subscription,
        CancellationTokenSource pumpCancellation)
    {
        var handlers = ChangeHintReceived;
        if (handlers is null)
            return;

        foreach (Action<FrontendChangeStreamDelivery> handler in handlers.GetInvocationList())
        {
            if (!IsCurrentLocked(generation, subscription, pumpCancellation))
                return;
            try
            {
                handler(delivery);
            }
            catch
            {
                // Observers cannot affect backend publication or subscription ownership.
            }
        }
    }

    private void RetireCurrentLocked(bool waitForPump)
    {
        var subscription = _subscription;
        var pumpCancellation = _pumpCancellation;
        var pumpTask = waitForPump ? _pumpTask : Task.CompletedTask;

        _subscription = null;
        _pumpCancellation = null;
        _pumpTask = Task.CompletedTask;
        _subscribedUserId = null;
        _currentPosition = null;
        _generation++;

        if (pumpCancellation is not null)
        {
            try { pumpCancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        if (subscription is not null)
            SafeUnsubscribe(subscription);

        if (subscription is not null || pumpCancellation is not null)
            QueueRetirementLocked(subscription, pumpTask, pumpCancellation);
    }

    private void QueueRetirementLocked(
        IFrontendChangeNotificationSubscription? subscription,
        Task pumpTask,
        CancellationTokenSource? pumpCancellation)
    {
        var previousRetirement = _retirementTask;
        _retirementTask = DrainRetiredAsync(
            previousRetirement,
            subscription,
            pumpTask,
            pumpCancellation);
    }

    private static async Task DrainRetiredAsync(
        Task previousRetirement,
        IFrontendChangeNotificationSubscription? subscription,
        Task pumpTask,
        CancellationTokenSource? pumpCancellation)
    {
        try { await previousRetirement.ConfigureAwait(false); } catch { }
        try { await pumpTask.ConfigureAwait(false); } catch { }
        if (subscription is not null)
        {
            try { await subscription.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        pumpCancellation?.Dispose();
    }

    private static void SafeUnsubscribe(IFrontendChangeNotificationSubscription subscription)
    {
        try { subscription.Unsubscribe(); } catch { }
    }

    private bool IsCurrentLocked(
        long generation,
        IFrontendChangeNotificationSubscription subscription,
        CancellationTokenSource pumpCancellation) =>
        Volatile.Read(ref _disposed) == 0 &&
        generation == _generation &&
        ReferenceEquals(_subscription, subscription) &&
        ReferenceEquals(_pumpCancellation, pumpCancellation) &&
        !pumpCancellation.IsCancellationRequested &&
        IsSessionActiveNoThrow() &&
        IsRuntimeReadyNoThrow();

    private bool IsSessionActiveNoThrow()
    {
        if (_sessionLifetime.IsCancellationRequested)
            return false;
        try { return _isSessionActive(); }
        catch { return false; }
    }

    private bool IsRuntimeReadyNoThrow()
    {
        try { return _runtimeClient.Snapshot.IsReady; }
        catch { return false; }
    }
}
