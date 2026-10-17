using PasswordManagerLocal.Common.Backend.Abstractions.Notifications;
using PasswordManagerLocal.Common.Contracts.Notifications;
using PasswordManagerLocal.Windows.Agent.Backend;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Lifecycle;
using PasswordManagerLocal.Windows.Ipc.Server;

namespace PasswordManagerLocal.Windows.Agent.Notifications;

/// <summary>
/// Bridges the backend's bounded user-filtered source to the already-registered UI IPC session.
/// It owns no replay history and tears down the source subscription when the UI disconnects.
/// </summary>
public sealed class WindowsAgentChangeNotificationAdapter :
    IWindowsIpcChangeNotificationUserSelector,
    IAsyncDisposable
{
    private const int BackendSubscriptionCapacity = 64;

    private readonly IWindowsAgentBackendRuntimeOwner _backendOwner;
    private readonly IUiConnectionCoordinator _uiCoordinator;
    private readonly IWindowsIpcNotificationSessionRegistry _sessionRegistry;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly object _scheduleGate = new();
    private readonly object _pumpGate = new();
    private CancellationTokenSource? _pumpSource;
    private IBackendChangeSubscription? _subscription;
    private Task _pumpTask = Task.CompletedTask;
    private Task _scheduledTransition = Task.CompletedTask;
    private Guid? _selectedUserId;
    private Guid? _selectedUserConnectionId;
    private Guid? _enabledConnectionId;
    private Guid? _activeConnectionId;
    private IBackendChangeSource? _activeSource;
    private int _disposed;

    public WindowsAgentChangeNotificationAdapter(
        IWindowsAgentBackendRuntimeOwner backendOwner,
        IUiConnectionCoordinator uiCoordinator,
        IWindowsIpcNotificationSessionRegistry sessionRegistry)
    {
        _backendOwner = backendOwner ?? throw new ArgumentNullException(nameof(backendOwner));
        _uiCoordinator = uiCoordinator ?? throw new ArgumentNullException(nameof(uiCoordinator));
        _sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        _backendOwner.StateChanged += HandleBackendStateChanged;
        _uiCoordinator.RegistrationChanged += HandleRegistrationChanged;
        ScheduleReconfigure();
    }

    public async Task<bool> SetUserFilterAsync(
        Guid connectionId,
        Guid? userId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (connectionId == Guid.Empty || userId == Guid.Empty || (!enabled && userId is not null))
            return false;

        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_uiCoordinator.RegisteredConnectionId != connectionId ||
                !_sessionRegistry.HasTarget(connectionId))
                return false;

            if (enabled && !_sessionRegistry.Enable(connectionId))
                return false;

            _enabledConnectionId = enabled ? connectionId : null;
            _selectedUserConnectionId = enabled ? connectionId : null;
            _selectedUserId = enabled ? userId : null;
            // Restart even when the filter is unchanged so the newly registered UI receives
            // an explicit initial watermark after its notification target is admitted.
            if (enabled)
                await ConfigureSubscriptionLockedAsync(forceRestart: true, cancellationToken);
            else
            {
                _sessionRegistry.Disable(connectionId);
                await StopSubscriptionLockedAsync();
                _sessionRegistry.ClearPending(connectionId);
            }
            return _uiCoordinator.RegisteredConnectionId == connectionId;
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    internal Task WaitForScheduledTransitionsAsync()
    {
        lock (_scheduleGate)
            return _scheduledTransition;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _backendOwner.StateChanged -= HandleBackendStateChanged;
        _uiCoordinator.RegistrationChanged -= HandleRegistrationChanged;
        CancelCurrentPump();
        Task scheduled;
        lock (_scheduleGate)
            scheduled = _scheduledTransition;
        try { await scheduled; } catch { }

        await _transitionGate.WaitAsync(CancellationToken.None);
        try
        {
            await StopSubscriptionLockedAsync();
        }
        finally
        {
            _transitionGate.Release();
            _transitionGate.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    private void HandleBackendStateChanged(object? sender, EventArgs args) => ScheduleReconfigure();

    private void HandleRegistrationChanged(
        object? sender,
        UiConnectionRegistrationChangedEventArgs args)
    {
        if (args.Previous?.ConnectionId != args.Current?.ConnectionId)
        {
            CancelCurrentPump();
        }
        ScheduleReconfigure();
    }

    private void ScheduleReconfigure()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        lock (_scheduleGate)
            _scheduledTransition = ReconfigureAfterAsync(_scheduledTransition);
        _ = _scheduledTransition.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ReconfigureAfterAsync(Task previous)
    {
        try { await previous; } catch { }
        if (Volatile.Read(ref _disposed) != 0)
            return;
        await _transitionGate.WaitAsync(CancellationToken.None);
        try
        {
            if (Volatile.Read(ref _disposed) == 0)
                await ConfigureSubscriptionLockedAsync(forceRestart: false, CancellationToken.None);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async Task ConfigureSubscriptionLockedAsync(
        bool forceRestart,
        CancellationToken cancellationToken)
    {
        var registration = _uiCoordinator.Registration;
        var source = _backendOwner.ChangeSource;
        if (_enabledConnectionId != registration?.ConnectionId)
        {
            _enabledConnectionId = null;
            _selectedUserConnectionId = null;
            _selectedUserId = null;
        }
        var userId = registration is not null &&
            _selectedUserConnectionId == registration.ConnectionId
                ? _selectedUserId
                : null;
        if (!forceRestart && _subscription is not null &&
            !_pumpTask.IsCompleted &&
            _activeConnectionId == registration?.ConnectionId &&
            ReferenceEquals(_activeSource, source) &&
            _subscriptionUserId == userId)
        {
            return;
        }

        await StopSubscriptionLockedAsync();
        if (registration is null || _enabledConnectionId != registration.ConnectionId || source is null ||
            !_sessionRegistry.IsEnabled(registration.ConnectionId) ||
            Volatile.Read(ref _disposed) != 0)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        var subscription = source.Subscribe(userId, BackendSubscriptionCapacity);
        var sourceToken = new CancellationTokenSource();
        var start = subscription.Start;
        _sessionRegistry.TryEnqueue(
            registration.ConnectionId,
            new IpcChangeNotificationDto(
                start.RuntimeInstanceId,
                start.Sequence,
                FrontendChangeScope.Everything,
                DateTimeOffset.UtcNow,
                RequiresAuthoritativeRefresh: true));
        lock (_pumpGate)
        {
            _subscription = subscription;
            _pumpSource = sourceToken;
            _activeConnectionId = registration.ConnectionId;
            _activeSource = source;
            _subscriptionUserId = userId;
            _pumpTask = PumpAsync(
                registration.ConnectionId,
                source,
                subscription,
                start.RuntimeInstanceId,
                start.Sequence,
                sourceToken.Token);
        }
    }

    private Guid? _subscriptionUserId;

    private async Task StopSubscriptionLockedAsync()
    {
        CancellationTokenSource? source;
        IBackendChangeSubscription? subscription;
        Task pump;
        lock (_pumpGate)
        {
            source = _pumpSource;
            subscription = _subscription;
            pump = _pumpTask;
            _pumpSource = null;
            _subscription = null;
            _activeConnectionId = null;
            _activeSource = null;
            _subscriptionUserId = null;
            _pumpTask = Task.CompletedTask;
        }

        if (source is not null)
        {
            try { source.Cancel(); } catch (ObjectDisposedException) { }
        }
        if (pump != Task.CompletedTask)
        {
            try { await pump; } catch (OperationCanceledException) { }
        }
        if (subscription is not null)
        {
            try { await subscription.DisposeAsync(); } catch { }
        }
        source?.Dispose();
    }

    private void CancelCurrentPump()
    {
        lock (_pumpGate)
        {
            try { _pumpSource?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task PumpAsync(
        Guid connectionId,
        IBackendChangeSource source,
        IBackendChangeSubscription subscription,
        Guid initialRuntimeInstanceId,
        long initialSequence,
        CancellationToken cancellationToken)
    {
        var lastRuntimeInstanceId = initialRuntimeInstanceId;
        var lastSequence = initialSequence;
        try
        {
            await foreach (var delivery in subscription.ReadAllAsync(cancellationToken))
            {
                var notification = delivery.Notification;
                var runtimeChanged = delivery.RuntimeInstanceId != lastRuntimeInstanceId;
                var sequenceGap = !runtimeChanged &&
                    (lastSequence == long.MaxValue || delivery.Sequence != lastSequence + 1);
                var requiresRefresh = delivery.RequiresAuthoritativeRefresh ||
                    notification is null || runtimeChanged || sequenceGap;
                var message = new IpcChangeNotificationDto(
                    delivery.RuntimeInstanceId,
                    delivery.Sequence,
                    requiresRefresh ? FrontendChangeScope.Everything : notification!.Scope,
                    notification?.OccurredAtUtc ?? DateTimeOffset.UtcNow,
                    requiresRefresh);
                if (!_sessionRegistry.TryEnqueue(connectionId, message))
                    return;

                if (runtimeChanged)
                {
                    lastRuntimeInstanceId = delivery.RuntimeInstanceId;
                    lastSequence = delivery.Sequence;
                }
                else if (delivery.Sequence > lastSequence)
                {
                    lastSequence = delivery.Sequence;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            try
            {
                var position = source.CurrentPosition;
                _sessionRegistry.TryEnqueue(
                    connectionId,
                    new IpcChangeNotificationDto(
                        position.RuntimeInstanceId,
                        position.Sequence,
                        FrontendChangeScope.Everything,
                        DateTimeOffset.UtcNow,
                        RequiresAuthoritativeRefresh: true));
            }
            catch
            {
                // The next successful subscription begins with a full-refresh watermark.
            }
            ScheduleReconfigure();
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WindowsAgentChangeNotificationAdapter));
    }
}
