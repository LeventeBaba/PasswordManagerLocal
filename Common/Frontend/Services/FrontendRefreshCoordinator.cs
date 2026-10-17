using PasswordManagerLocal.Common.Contracts.Notifications;
using PasswordManagerLocal.Common.Contracts.Runtime;

namespace PasswordManagerLocal.Common.Frontend.Services;

/// <summary>
/// Coalesces backend invalidations for exactly one frontend UI session. Change deliveries remain
/// hints; this coordinator only asks the active UI graph to read authoritative endpoint state.
/// </summary>
public sealed class FrontendRefreshCoordinator : IAsyncDisposable
{
    public static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(200);

    private static readonly (FrontendRefreshPage Page, FrontendChangeScope Scope)[] PageScopes =
    [
        (FrontendRefreshPage.Passwords, FrontendChangeScope.Passwords),
        (FrontendRefreshPage.UserProfile, FrontendChangeScope.UserProfile),
        (FrontendRefreshPage.Devices, FrontendChangeScope.Devices),
        (FrontendRefreshPage.Devices, FrontendChangeScope.DevicePresence),
        (FrontendRefreshPage.Enrollment, FrontendChangeScope.Enrollment),
        (FrontendRefreshPage.AuthenticationSessions, FrontendChangeScope.AuthenticationSessions),
        (FrontendRefreshPage.LocalDevice, FrontendChangeScope.LocalDevice),
        (FrontendRefreshPage.BackgroundSync, FrontendChangeScope.BackgroundSync)
    ];

    private readonly IFrontendChangeHintSource _source;
    private readonly IFrontendRefreshTarget _target;
    private readonly IFrontendRefreshDispatcher _dispatcher;
    private readonly IFrontendRefreshDelay _delay;
    private readonly TimeSpan _debounceInterval;
    private readonly CancellationToken _sessionLifetime;
    private readonly object _gate = new();
    private readonly Dictionary<FrontendRefreshPage, FrontendChangeScope> _dirty = new();
    private readonly HashSet<FrontendRefreshPage> _deferredDirtyPages = [];
    private HashSet<FrontendRefreshPage> _visiblePages = [];
    private CancellationTokenSource? _debounceCancellation;
    private Task? _debounceTask;
    private CancellationTokenSource? _batchCancellation;
    private Task? _runner;
    private FrontendChangeScope _pendingScopes;
    private FrontendChangeScope _activeScopes;
    private Guid? _runtimeInstanceId;
    private long _lastSequence;
    private long _navigationGeneration;
    private long _authenticationGeneration;
    private long _authoritativeGeneration;
    private Guid _observedAuthenticationToken;
    private Guid? _observedAuthenticationUserId;
    private bool _hasSequence;
    private bool _hasAuthenticationSnapshot;
    private bool _started;
    private bool _pendingRerun;
    private bool _disposed;

    public FrontendRefreshCoordinator(
        IFrontendChangeHintSource source,
        IFrontendRefreshTarget target,
        IFrontendRefreshDispatcher dispatcher,
        IFrontendRefreshDelay? delay = null,
        TimeSpan? debounceInterval = null,
        CancellationToken sessionLifetime = default)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _delay = delay ?? new TaskFrontendRefreshDelay();
        _debounceInterval = debounceInterval ?? DefaultDebounceInterval;
        if (_debounceInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounceInterval));
        _sessionLifetime = sessionLifetime;
        _source.ChangeHintReceived += HandleChangeHintReceived;
    }

    public bool IsDisposed
    {
        get { lock (_gate) return _disposed; }
    }

    public bool IsDirty(FrontendRefreshPage page)
    {
        lock (_gate)
            return _dirty.TryGetValue(page, out var scopes) && scopes != FrontendChangeScope.None;
    }

    public FrontendChangeScope DirtyScopes(FrontendRefreshPage page)
    {
        lock (_gate)
            return _dirty.GetValueOrDefault(page);
    }

    /// <summary>Waits for scheduled debounce and refresh work; useful for deterministic callers and tests.</summary>
    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task? pending;
            lock (_gate)
                pending = _runner ?? _debounceTask;
            if (pending is null)
                return;
            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Begins processing after the session's authoritative first page load has finished.</summary>
    public void Start(
        IEnumerable<FrontendRefreshPage> visiblePages,
        FrontendChangeStreamStart? streamPosition = null)
    {
        ArgumentNullException.ThrowIfNull(visiblePages);
        lock (_gate)
        {
            if (_disposed || _started)
                return;

            _visiblePages = visiblePages.ToHashSet();
            if (streamPosition is not null && !_hasSequence)
            {
                _runtimeInstanceId = streamPosition.RuntimeInstanceId;
                _lastSequence = streamPosition.Sequence;
                _hasSequence = true;
                if (streamPosition.RequiresAuthoritativeRefresh)
                    QueueScopesLocked(FrontendChangeScope.Everything);
            }

            _started = true;
            QueueDirtyVisibleScopesLocked();
            ScheduleLocked();
        }
    }

    /// <summary>Reconciles the current visible state after resume or another lost-history boundary.</summary>
    public void RequestAuthoritativeRefresh()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _authoritativeGeneration++;
            if (_activeScopes != FrontendChangeScope.None)
                QueueScopesLocked(_activeScopes);
            _batchCancellation?.Cancel();
            QueueScopesLocked(FrontendChangeScope.Everything);
            ScheduleLocked();
        }
    }

    /// <summary>Invalidates in-flight work when navigation changes within this UI session.</summary>
    public void OnNavigationChanged(IEnumerable<FrontendRefreshPage> visiblePages)
    {
        ArgumentNullException.ThrowIfNull(visiblePages);
        lock (_gate)
        {
            if (_disposed)
                return;

            _navigationGeneration++;
            _deferredDirtyPages.Clear();
            if (_activeScopes != FrontendChangeScope.None)
                QueueScopesLocked(_activeScopes);
            _batchCancellation?.Cancel();
            _visiblePages = visiblePages.ToHashSet();
            QueueDirtyVisibleScopesLocked();
            ScheduleLocked();
        }
    }

    /// <summary>Retries dirty visible sections when an editor, dialog, or operation becomes safe.</summary>
    public void OnPageStateChanged()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            foreach (var page in _visiblePages)
            {
                var dirtyScopes = _dirty.GetValueOrDefault(page);
                if (dirtyScopes == FrontendChangeScope.None)
                    continue;

                if ((_activeScopes & dirtyScopes) != FrontendChangeScope.None)
                {
                    _deferredDirtyPages.Add(page);
                    continue;
                }

                if (SafeToRefreshNoThrow(page))
                    _pendingScopes |= dirtyScopes & ~_pendingScopes;
            }
            ScheduleLocked();
        }
    }

    /// <summary>Discards work from the previous selected user or authentication token.</summary>
    public void OnAuthenticationChanged() =>
        OnAuthenticationChanged(_target.CurrentAuthenticationToken, _target.CurrentUserId);

    public void OnAuthenticationChanged(Guid token, Guid? userId)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            if (_hasAuthenticationSnapshot &&
                _observedAuthenticationToken == token &&
                _observedAuthenticationUserId == userId)
            {
                return;
            }

            var sameSelectedUser = _hasAuthenticationSnapshot &&
                                   userId is not null &&
                                   _observedAuthenticationUserId == userId;

            _hasAuthenticationSnapshot = true;
            _observedAuthenticationToken = token;
            _observedAuthenticationUserId = userId;

            _authenticationGeneration++;
            _dirty.Clear();
            _deferredDirtyPages.Clear();
            _pendingRerun = false;
            _batchCancellation?.Cancel();
            _debounceCancellation?.Cancel();

            if (sameSelectedUser)
            {
                // A renewed token for the same account invalidates old requests, but the
                // current stream remains authorized and its sequence watermark is still valid.
                _pendingScopes = FrontendChangeScope.None;
                QueueScopesLocked(FrontendChangeScope.Everything);
                ScheduleLocked();
            }
            else
            {
                _pendingScopes = FrontendChangeScope.None;
                _hasSequence = false;
                _runtimeInstanceId = null;
                _lastSequence = 0;
            }
        }
    }

    public void Deactivate()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _source.ChangeHintReceived -= HandleChangeHintReceived;
            _pendingScopes = FrontendChangeScope.None;
            _dirty.Clear();
            _deferredDirtyPages.Clear();
            _batchCancellation?.Cancel();
            _debounceCancellation?.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Deactivate();
        Task? runner;
        Task? debounce;
        lock (_gate)
        {
            runner = _runner;
            debounce = _debounceTask;
        }
        if (runner is not null)
        {
            try { await runner.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (debounce is not null)
        {
            try { await debounce.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        GC.SuppressFinalize(this);
    }

    private void HandleChangeHintReceived(FrontendChangeStreamDelivery delivery)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            var runtimeChanged = _runtimeInstanceId is { } previousRuntime &&
                                 previousRuntime != delivery.RuntimeInstanceId;
            var foreignUserNotification = delivery.Notification?.UserId is { } notificationUserId &&
                                          _hasAuthenticationSnapshot &&
                                          _observedAuthenticationUserId != notificationUserId;
            var sequenceGap = _hasSequence && !runtimeChanged &&
                              delivery.Sequence > _lastSequence &&
                              delivery.Sequence != _lastSequence + 1;
            var duplicate = _hasSequence && !runtimeChanged &&
                            delivery.Sequence <= _lastSequence &&
                            !delivery.RequiresAuthoritativeRefresh;
            if (duplicate)
                return;

            var requiresFullRefresh = delivery.RequiresAuthoritativeRefresh ||
                                      delivery.Notification is null ||
                                      runtimeChanged || sequenceGap;
            if (requiresFullRefresh)
            {
                _authoritativeGeneration++;
                if (_activeScopes != FrontendChangeScope.None)
                    QueueScopesLocked(_activeScopes);
                _batchCancellation?.Cancel();
            }

            _runtimeInstanceId = delivery.RuntimeInstanceId;
            _lastSequence = delivery.Sequence;
            _hasSequence = true;
            if (foreignUserNotification && !requiresFullRefresh)
                return;

            var scopes = requiresFullRefresh
                ? FrontendChangeScope.Everything
                : delivery.Notification!.Scope;
            QueueScopesLocked(scopes);
            ScheduleLocked();
        }
    }

    private void QueueScopesLocked(FrontendChangeScope scopes)
    {
        scopes &= FrontendChangeScope.Everything;
        if (scopes == FrontendChangeScope.None)
            return;

        _pendingScopes |= scopes;
        foreach (var mapping in PageScopes)
        {
            var mapped = scopes & mapping.Scope;
            if (mapped != FrontendChangeScope.None)
                _dirty[mapping.Page] = _dirty.GetValueOrDefault(mapping.Page) | mapped;
        }
    }

    private void QueueDirtyVisibleScopesLocked()
    {
        foreach (var page in _visiblePages)
        {
            var scopes = _dirty.GetValueOrDefault(page) & ~_pendingScopes & ~_activeScopes;
            if (scopes != FrontendChangeScope.None && SafeToRefreshNoThrow(page))
                _pendingScopes |= scopes;
        }
    }

    private void QueueDeferredDirtyPagesLocked()
    {
        foreach (var page in _deferredDirtyPages.ToArray())
        {
            _deferredDirtyPages.Remove(page);
            var scopes = _dirty.GetValueOrDefault(page) & ~_pendingScopes;
            if (scopes != FrontendChangeScope.None &&
                _visiblePages.Contains(page) &&
                SafeToRefreshNoThrow(page))
            {
                _pendingScopes |= scopes;
            }
        }
    }

    private bool SafeToRefreshNoThrow(FrontendRefreshPage page)
    {
        try { return _target.IsActive && _target.CanRefreshPage(page); }
        catch { return false; }
    }

    private void ScheduleLocked()
    {
        if (!_started || _disposed || _pendingScopes == FrontendChangeScope.None ||
            _sessionLifetime.IsCancellationRequested)
        {
            return;
        }

        // A newer hint restarts the debounce window even when a refresh runner is
        // waiting between batches. The runner will resume only when this window ends.
        if (_runner is not null)
            _pendingRerun = false;

        _debounceCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_sessionLifetime);
        _debounceCancellation = cancellation;
        _debounceTask = Task.Run(() => DebounceAndStartAsync(cancellation));
    }

    private async Task DebounceAndStartAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await _delay.DelayAsync(_debounceInterval, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_debounceCancellation, cancellation))
                {
                    _debounceCancellation = null;
                    _debounceTask = null;
                }
            }
            cancellation.Dispose();
            return;
        }

        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_debounceCancellation, cancellation))
            {
                cancellation.Dispose();
                return;
            }
            _debounceCancellation = null;
            _debounceTask = null;
            cancellation.Dispose();

            if (_runner is not null)
            {
                _pendingRerun = true;
                return;
            }

            _runner = Task.Run(RunBatchesAsync);
        }
    }

    private async Task RunBatchesAsync()
    {
        while (true)
        {
            FrontendChangeScope scopes = FrontendChangeScope.None;
            CancellationTokenSource? batchCancellation = null;
            Task? debounceToAwait = null;
            lock (_gate)
            {
                if (_disposed || _sessionLifetime.IsCancellationRequested)
                {
                    _runner = null;
                    return;
                }

                if (_pendingScopes == FrontendChangeScope.None)
                {
                    _runner = null;
                    _pendingRerun = false;
                    return;
                }

                if (!_pendingRerun && _debounceTask is not null)
                {
                    // Keep this runner as the single flight while the new burst settles.
                    // If another hint replaces the debounce task, loop and await its latest task.
                    debounceToAwait = _debounceTask;
                }
                else
                {
                    _pendingRerun = false;
                    scopes = _pendingScopes;
                    _pendingScopes = FrontendChangeScope.None;
                    _activeScopes = scopes;
                    batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_sessionLifetime);
                    _batchCancellation = batchCancellation;
                }
            }

            if (debounceToAwait is not null)
            {
                try { await debounceToAwait.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                continue;
            }

            var activeBatchCancellation = batchCancellation!;
            try
            {
                await ProcessBatchAsync(scopes, activeBatchCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (activeBatchCancellation.IsCancellationRequested)
            {
                // Dirty flags remain set; navigation/resume/auth changes determine the next safe read.
            }
            catch
            {
                // Endpoint failures leave the affected scope dirty without replacing visible state.
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_batchCancellation, activeBatchCancellation))
                        _batchCancellation = null;
                    _activeScopes = FrontendChangeScope.None;
                }
                activeBatchCancellation.Dispose();
            }

            lock (_gate)
            {
                if (_disposed || _sessionLifetime.IsCancellationRequested)
                {
                    _runner = null;
                    return;
                }

                QueueDeferredDirtyPagesLocked();

                if (_pendingScopes == FrontendChangeScope.None)
                {
                    _runner = null;
                    _pendingRerun = false;
                    return;
                }

                if (!_pendingRerun && _debounceTask is null)
                    ScheduleLocked();
            }
        }
    }

    private async Task ProcessBatchAsync(FrontendChangeScope scopes, CancellationToken cancellationToken)
    {
        var authenticationRevalidationSucceeded = true;
        var runtimeId = GetRuntimeId();
        var sessionGeneration = CaptureGeneration();
        var isCurrent = CreateCurrentPredicate(
            runtimeId,
            sessionGeneration.Navigation,
            sessionGeneration.Authentication,
            sessionGeneration.Authoritative);

        if ((scopes & FrontendChangeScope.AuthenticationSessions) != FrontendChangeScope.None)
        {
            try
            {
                _ = await _dispatcher.InvokeAsync(
                    () => _target.RevalidateCurrentSessionAsync(isCurrent, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Authentication status failure leaves that scope dirty, but cannot block
                // independent password, profile, device, enrollment, or settings reads.
                authenticationRevalidationSucceeded = false;
            }

            if (!isCurrent())
                return;
        }

        foreach (var group in PageScopes
                     .Where(mapping => (scopes & mapping.Scope) != FrontendChangeScope.None)
                     .GroupBy(mapping => mapping.Page))
        {
            var page = group.Key;
            var pageScopes = group.Aggregate(
                FrontendChangeScope.None,
                (aggregate, mapping) => aggregate | (scopes & mapping.Scope));
            if (pageScopes == FrontendChangeScope.None)
                continue;

            bool visible;
            lock (_gate)
                visible = _visiblePages.Contains(page);
            if (!visible)
                continue;

            var pageCurrent = () => isCurrent() && IsPageVisible(page);
            try
            {
                var refreshed = await _dispatcher.InvokeAsync(async () =>
                {
                    if (!pageCurrent() || !_target.CanRefreshPage(page))
                        return false;

                    return await _target.RefreshPageAsync(
                        page,
                        pageScopes,
                        pageCurrent,
                        cancellationToken);
                }, cancellationToken).ConfigureAwait(false);

                if (refreshed && pageCurrent())
                {
                    var clearedScopes = pageScopes;
                    if (!authenticationRevalidationSucceeded)
                        clearedScopes &= ~FrontendChangeScope.AuthenticationSessions;
                    ClearDirty(page, clearedScopes);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Keep the page dirty and let a later notification, resume, or navigation retry it.
            }
        }
    }

    private bool IsPageVisible(FrontendRefreshPage page)
    {
        lock (_gate)
            return !_disposed && _visiblePages.Contains(page);
    }

    private void ClearDirty(FrontendRefreshPage page, FrontendChangeScope scopes)
    {
        lock (_gate)
        {
            if (!_dirty.TryGetValue(page, out var current))
                return;
            // A newer invalidation can arrive while this page is in flight. Do not let the
            // older result clear that newer dirty bit; its pending rerun owns the clear.
            current &= ~(scopes & ~_pendingScopes);
            if (current == FrontendChangeScope.None)
                _dirty.Remove(page);
            else
                _dirty[page] = current;
        }
    }

    private Guid? GetRuntimeId()
    {
        lock (_gate)
            return _runtimeInstanceId;
    }

    private (long Navigation, long Authentication, long Authoritative) CaptureGeneration()
    {
        lock (_gate)
            return (_navigationGeneration, _authenticationGeneration, _authoritativeGeneration);
    }

    private Func<bool> CreateCurrentPredicate(
        Guid? runtimeId,
        long navigationGeneration,
        long authenticationGeneration,
        long authoritativeGeneration) =>
        () =>
        {
            lock (_gate)
            {
                if (_disposed ||
                    _navigationGeneration != navigationGeneration ||
                    _authenticationGeneration != authenticationGeneration ||
                    _authoritativeGeneration != authoritativeGeneration ||
                    _runtimeInstanceId != runtimeId)
                {
                    return false;
                }
            }
            if (_sessionLifetime.IsCancellationRequested)
                return false;
            try { return _target.IsActive; }
            catch { return false; }
        };
}
