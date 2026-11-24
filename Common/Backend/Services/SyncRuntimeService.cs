using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.Notifications;
using PasswordManagerLocal.Common.Backend.Abstractions.State;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Contracts.Notifications;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class SyncRuntimeService : ISyncRuntimeService, ISyncRuntimeHealth
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeviceIdentityService _identity;
    private readonly IEnrollmentRuntimeState _enrollmentState;
    private readonly ISyncDeviceIdentityService _syncDeviceIdentities;
    private readonly IDiscoveredDeviceEndpointRegistry _endpointRegistry;
    private readonly IDeviceSyncTaskService _deviceSyncTasks;
    private readonly IDevicePresenceRegistry? _presenceRegistry;
    private readonly IBackendChangePublisher? _changePublisher;
    private readonly IPassiveSyncRuntime? _passive;
    private readonly IReadOnlyList<ISyncControlledHostedService> _controlledServices;
    private readonly List<ISyncControlledHostedService> _activeServices = [];
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _stateLock = new();
    private SyncRuntimeSnapshot _snapshot = new(SyncRuntimeState.Starting, null);
    private SyncRuntimeReason _reasons;
    private SyncRuntimeEligibilityReason _eligibility = SyncRuntimeEligibilityReason.NoLocalProfiles;
    private bool _running;
    private bool _passiveStarted;
    private bool _restartRequired;
    private bool _unsafeHealthDetected;

    public SyncRuntimeService(IServiceScopeFactory scopeFactory, IDeviceIdentityService identity,
        IEnrollmentRuntimeState enrollmentState, ISyncDeviceIdentityService syncDeviceIdentities,
        IDiscoveredDeviceEndpointRegistry endpointRegistry, IDeviceSyncTaskService deviceSyncTasks,
        IEnumerable<ISyncControlledHostedService> controlledServices,
        IDevicePresenceRegistry? presenceRegistry = null, IBackendChangePublisher? changePublisher = null,
        IPassiveSyncRuntime? passive = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _enrollmentState = enrollmentState ?? throw new ArgumentNullException(nameof(enrollmentState));
        _syncDeviceIdentities = syncDeviceIdentities ?? throw new ArgumentNullException(nameof(syncDeviceIdentities));
        _endpointRegistry = endpointRegistry ?? throw new ArgumentNullException(nameof(endpointRegistry));
        _deviceSyncTasks = deviceSyncTasks ?? throw new ArgumentNullException(nameof(deviceSyncTasks));
        _presenceRegistry = presenceRegistry;
        _changePublisher = changePublisher;
        _passive = passive;
        ArgumentNullException.ThrowIfNull(controlledServices);
        _controlledServices = controlledServices.OrderBy(service => service.StartOrder).ToArray();
    }

    public SyncRuntimeSnapshot Snapshot { get { lock (_stateLock) return _snapshot; } }
    public bool RequiresProcessRestart => Snapshot.RequiresProcessRestart;
    public bool IsHealthy => !Snapshot.RequiresProcessRestart &&
        (Snapshot.State is SyncRuntimeState.Starting or SyncRuntimeState.Stopping ||
         Snapshot.State != SyncRuntimeState.Degraded && (!_running || ControlledServicesHealthy()) &&
         (!_passiveStarted || _passive is { IsHealthy: true, RequiresProcessRestart: false }));
    public event EventHandler<SyncRuntimeStateChangedEventArgs>? StateChanged;

    public Task RefreshSyncEnabledAsync(CancellationToken ct = default) => ExecuteAsync(async changes =>
    {
        SyncRuntimeEligibilityResult eligibility;
        bool deliveryPending;
        bool activeWork;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            // This must be a fresh committed read, including on process startup and retry.
            eligibility = await scope.ServiceProvider.GetRequiredService<ISyncRuntimeEligibilityService>().EvaluateAsync(ct);
            var delivery = scope.ServiceProvider.GetService<IRevocationDeliveryService>();
            deliveryPending = delivery is not null && await delivery.HasPendingDeliveryAsync(ct);
            activeWork = scope.ServiceProvider.GetService<IRevocationDeliveryActivity>()?.HasActiveWork == true;
        }
        catch { _eligibility = SyncRuntimeEligibilityReason.EvaluationFailed; throw; }
        _eligibility = eligibility.Reason;
        if (_eligibility == SyncRuntimeEligibilityReason.EvaluationFailed)
            throw new InvalidDataException("Synchronization eligibility could not be verified.");
        _reasons = (eligibility.IsEligible ? SyncRuntimeReason.NormalProfileSynchronization : SyncRuntimeReason.None) |
            (deliveryPending ? SyncRuntimeReason.PendingRevocationDelivery : SyncRuntimeReason.None) |
            (_enrollmentState.IsActive ? SyncRuntimeReason.ActiveEnrollment : SyncRuntimeReason.None) |
            (activeWork && _identity.IsSyncOn ? SyncRuntimeReason.ActiveInFlightSynchronization : SyncRuntimeReason.None);

        _unsafeHealthDetected = _controlledServices.OfType<ISyncRuntimeHealth>().Any(service => service.RequiresProcessRestart) ||
            _passive?.RequiresProcessRestart == true;
        if (_unsafeHealthDetected)
            throw new IOException("Synchronization network ownership requires a complete process restart.");

        // Failure of a live task invalidates Running, even if a listener object/lease still exists.
        if (_reasons != SyncRuntimeReason.None && _running && !ControlledServicesHealthy())
            throw new IOException("A required synchronization network service stopped unexpectedly.");

        var wasSyncOn = _identity.IsSyncOn;
        var syncOn = (_reasons & ~SyncRuntimeReason.ActiveEnrollment) != 0;
        if (wasSyncOn != syncOn)
            await _identity.SetSyncOnAsync(syncOn, ct);

        if (!eligibility.HasHistoricalControlEvidence && _passiveStarted)
            await StopPassiveAsync(ct);
        if (eligibility.HasHistoricalControlEvidence && !_passiveStarted && _passive is not null)
        {
            _passiveStarted = true; // Include a partially started passive owner in cleanup.
            await _passive.StartAsync(ct);
        }
        if (_reasons != SyncRuntimeReason.None)
            await StartCoreAsync(changes, ct);
        else
            await StopCoreAsync(changes, ct, reconcileAll: wasSyncOn);
        if (_passiveStarted && _passive?.IsHealthy == false)
            throw new IOException("A required passive synchronization listener is unhealthy.");
    }, ct);

    public Task BeginEnrollmentOnlyAsync(CancellationToken ct = default) => ExecuteAsync(async changes =>
    {
        _enrollmentState.Activate();
        _reasons |= SyncRuntimeReason.ActiveEnrollment;
        await StartCoreAsync(changes, ct);
    }, ct);

    public async Task EndEnrollmentOnlyAsync(CancellationToken ct = default)
    {
        // Deactivation and the fresh read share the same serialized reconciliation path.
        await ExecuteAsync(_ =>
        {
            _enrollmentState.Deactivate();
            _reasons &= ~SyncRuntimeReason.ActiveEnrollment;
            return Task.CompletedTask;
        }, ct);
        await RefreshSyncEnabledAsync(ct);
    }

    public Task StartAsync(CancellationToken ct = default) => RefreshSyncEnabledAsync(ct);

    public Task StopAsync(CancellationToken ct = default) => ExecuteAsync(async changes =>
    {
        _enrollmentState.Deactivate();
        _reasons = SyncRuntimeReason.None;
        await StopCoreAsync(changes, ct, reconcileAll: true);
        await StopPassiveAsync(ct);
        Transition(SyncRuntimeState.Disabled, null, changes);
    }, ct, allowUnsafeCleanup: true);

    private async Task ExecuteAsync(Func<List<SyncRuntimeStateChangedEventArgs>, Task> operation,
        CancellationToken ct, bool allowUnsafeCleanup = false)
    {
        var changes = new List<SyncRuntimeStateChangedEventArgs>();
        await _lifecycleLock.WaitAsync(ct);
        var unsafeAtEntry = _restartRequired;
        try
        {
            if (_restartRequired && !allowUnsafeCleanup)
                throw new InvalidOperationException("Synchronization requires a complete process restart after an unsafe failure.");
            await operation(changes);
        }
        catch (Exception failure)
        {
            // Never leave a partial startup running. A failed stop latches the unsafe state;
            // neither another setting event nor the supervisor can clear it on this instance.
            if (!_restartRequired)
            {
                try
                {
                    if (_activeServices.Count != 0 || _running)
                        await StopControlledServicesAsync(CancellationToken.None);
                }
                catch (Exception cleanupFailure)
                {
                    _restartRequired = true;
                    failure = new AggregateException(failure, cleanupFailure);
                }
            }
            if (!unsafeAtEntry)
            {
                try { await StopPassiveAsync(CancellationToken.None); }
                catch (Exception cleanupFailure)
                {
                    _restartRequired = true;
                    failure = new AggregateException(failure, cleanupFailure);
                }
            }
            _running = false;
            _restartRequired |= _unsafeHealthDetected;
            if (failure is InvalidDataException or PasswordManagerLocal.Common.Contracts.Errors.InvalidDataIntegrityException or
                Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 11 or 26 })
            {
                _eligibility = SyncRuntimeEligibilityReason.EvaluationFailed;
                _restartRequired = true;
            }
            if (failure is UnauthorizedAccessException or NotSupportedException)
                _restartRequired = true; // Requires a platform/configuration change and a new owner.
            Transition(SyncRuntimeState.Degraded, failure, changes);
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
            Publish(changes);
        }
    }

    private bool ControlledServicesHealthy() => _controlledServices.All(service => service is not ISyncRuntimeHealth health || health.IsHealthy && !health.RequiresProcessRestart);

    private async Task StartCoreAsync(List<SyncRuntimeStateChangedEventArgs> changes, CancellationToken ct)
    {
        if (!_identity.IsSyncOn && !_enrollmentState.IsActive)
        {
            await StopCoreAsync(changes, ct);
            return;
        }
        if (_running)
        {
            if (!ControlledServicesHealthy())
                throw new IOException("A required synchronization service is unhealthy.");
            Transition(SyncRuntimeState.Running, null, changes);
            return;
        }
        if (_activeServices.Count != 0)
            await StopControlledServicesAsync(CancellationToken.None);
        Transition(SyncRuntimeState.Starting, null, changes);
        foreach (var service in _controlledServices)
        {
            _activeServices.Add(service);
            await service.StartAsync(ct);
        }
        if (!ControlledServicesHealthy())
            throw new IOException("A required synchronization service terminated during startup.");
        _running = true;
        Transition(SyncRuntimeState.Running, null, changes);
    }

    private async Task StopCoreAsync(List<SyncRuntimeStateChangedEventArgs> changes, CancellationToken ct, bool reconcileAll = false)
    {
        if (_running || _activeServices.Count != 0 || reconcileAll)
        {
            Transition(SyncRuntimeState.Stopping, null, changes);
            if (_activeServices.Count == 0 && reconcileAll)
                _activeServices.AddRange(_controlledServices);
            await StopControlledServicesAsync(ct);
        }
        _running = false;
        Transition(SyncRuntimeState.Disabled, null, changes);
    }

    private async Task StopPassiveAsync(CancellationToken ct)
    {
        if (!_passiveStarted || _passive is null) return;
        try { await _passive.StopAsync(ct); _passiveStarted = false; }
        catch { _restartRequired = true; throw; }
    }

    private async Task StopControlledServicesAsync(CancellationToken ct)
    {
        var failures = new List<Exception>();
        try { await _deviceSyncTasks.StopAllAsync(ct); }
        catch (Exception failure) { failures.Add(failure); }
        var failed = new HashSet<ISyncControlledHostedService>();
        for (var index = _activeServices.Count - 1; index >= 0; index--)
        {
            var service = _activeServices[index];
            try { await service.StopAsync(ct); }
            catch (Exception failure) { failures.Add(failure); failed.Add(service); }
        }
        _activeServices.RemoveAll(service => !failed.Contains(service));
        _running = false;
        if (failures.Count != 0)
        {
            _restartRequired = true;
            throw new AggregateException("Synchronization resources did not stop safely.", failures);
        }
        _syncDeviceIdentities.Clear();
        _presenceRegistry?.InvalidateAll("synchronization runtime stopped");
        _endpointRegistry.Clear();
    }

    private void Transition(SyncRuntimeState state, Exception? failure, ICollection<SyncRuntimeStateChangedEventArgs> changes)
    {
        lock (_stateLock)
        {
            var next = new SyncRuntimeSnapshot(state, failure, _reasons, _eligibility, _restartRequired,
                _passiveStarted && _passive?.IsHealthy == true);
            if (_snapshot == next) return;
            var previous = _snapshot;
            _snapshot = next;
            changes.Add(new SyncRuntimeStateChangedEventArgs(previous, next));
        }
    }

    private void Publish(IEnumerable<SyncRuntimeStateChangedEventArgs> changes)
    {
        foreach (var change in changes)
        {
            _changePublisher?.Publish(FrontendChangeScope.BackgroundSync);
            if (StateChanged is not { } handlers) continue;
            foreach (EventHandler<SyncRuntimeStateChangedEventArgs> handler in handlers.GetInvocationList())
            {
                try { handler(this, change); } catch { }
            }
        }
    }
}
