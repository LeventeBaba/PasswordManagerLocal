using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.State;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class SyncRuntimeService : ISyncRuntimeService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeviceIdentityService _identity;
    private readonly IEnrollmentRuntimeState _enrollmentState;
    private readonly ISyncDeviceIdentityService _syncDeviceIdentities;
    private readonly IDiscoveredDeviceEndpointRegistry _endpointRegistry;
    private readonly IDeviceSyncTaskService _deviceSyncTasks;
    private readonly IDevicePresenceRegistry? _presenceRegistry;
    private readonly IReadOnlyList<ISyncControlledHostedService> _controlledServices;
    private readonly List<ISyncControlledHostedService> _activeServices = [];
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _stateLock = new();
    private SyncRuntimeSnapshot _snapshot = new(SyncRuntimeState.Disabled, null);
    private bool _running;

    public SyncRuntimeService(
        IServiceScopeFactory scopeFactory,
        IDeviceIdentityService identity,
        IEnrollmentRuntimeState enrollmentState,
        ISyncDeviceIdentityService syncDeviceIdentities,
        IDiscoveredDeviceEndpointRegistry endpointRegistry,
        IDeviceSyncTaskService deviceSyncTasks,
        IEnumerable<ISyncControlledHostedService> controlledServices,
        IDevicePresenceRegistry? presenceRegistry = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _enrollmentState = enrollmentState ?? throw new ArgumentNullException(nameof(enrollmentState));
        _syncDeviceIdentities = syncDeviceIdentities ?? throw new ArgumentNullException(nameof(syncDeviceIdentities));
        _endpointRegistry = endpointRegistry ?? throw new ArgumentNullException(nameof(endpointRegistry));
        _deviceSyncTasks = deviceSyncTasks ?? throw new ArgumentNullException(nameof(deviceSyncTasks));
        _presenceRegistry = presenceRegistry;
        ArgumentNullException.ThrowIfNull(controlledServices);
        _controlledServices = controlledServices
            .OrderBy(service => service.StartOrder)
            .ToArray();
    }

    public SyncRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_stateLock)
                return _snapshot;
        }
    }

    public event EventHandler<SyncRuntimeStateChangedEventArgs>? StateChanged;

    public async Task RefreshSyncEnabledAsync(CancellationToken ct = default)
    {
        var stateChanges = new List<SyncRuntimeStateChangedEventArgs>();
        var lifecycleLockTaken = false;

        try
        {
            bool shouldEnable;
            using (var scope = _scopeFactory.CreateScope())
            {
                var localUsers = scope.ServiceProvider.GetRequiredService<ILocalUserDeviceRepository>();
                var controlOperations = scope.ServiceProvider.GetService<IUserControlOperationRepository>();
                shouldEnable = await localUsers.AnySyncOnAsync(ct) ||
                               (controlOperations is not null &&
                                await controlOperations.HasAppliedAccountDeletionAsync(ct));
            }

            await _lifecycleLock.WaitAsync(ct);
            lifecycleLockTaken = true;

            if (_identity.IsSyncOn != shouldEnable)
                await _identity.SetSyncOnAsync(shouldEnable, ct);

            if (shouldEnable || _enrollmentState.IsActive)
                await StartCoreAsync(stateChanges, ct);
            else
                await StopCoreAsync(stateChanges, ct, reconcileAll: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Transition(SyncRuntimeState.Degraded, exception, stateChanges);
            throw;
        }
        finally
        {
            if (lifecycleLockTaken)
                _lifecycleLock.Release();

            Publish(stateChanges);
        }
    }

    public async Task BeginEnrollmentOnlyAsync(CancellationToken ct = default)
    {
        var stateChanges = new List<SyncRuntimeStateChangedEventArgs>();
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            _enrollmentState.Activate();
            await StartCoreAsync(stateChanges, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Transition(SyncRuntimeState.Degraded, exception, stateChanges);
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
            Publish(stateChanges);
        }
    }

    public async Task EndEnrollmentOnlyAsync(CancellationToken ct = default)
    {
        var stateChanges = new List<SyncRuntimeStateChangedEventArgs>();
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            _enrollmentState.Deactivate();
            if (_identity.IsSyncOn)
                await StartCoreAsync(stateChanges, ct);
            else
                await StopCoreAsync(stateChanges, ct, reconcileAll: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Transition(SyncRuntimeState.Degraded, exception, stateChanges);
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
            Publish(stateChanges);
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var stateChanges = new List<SyncRuntimeStateChangedEventArgs>();
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            await StartCoreAsync(stateChanges, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Transition(SyncRuntimeState.Degraded, exception, stateChanges);
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
            Publish(stateChanges);
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        var stateChanges = new List<SyncRuntimeStateChangedEventArgs>();
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            _enrollmentState.Deactivate();
            await StopCoreAsync(stateChanges, ct, reconcileAll: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Transition(SyncRuntimeState.Degraded, exception, stateChanges);
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
            Publish(stateChanges);
        }
    }

    private async Task StartCoreAsync(
        ICollection<SyncRuntimeStateChangedEventArgs> stateChanges,
        CancellationToken ct)
    {
        if (!_identity.IsSyncOn && !_enrollmentState.IsActive)
        {
            if (_running || _activeServices.Count > 0)
                await StopCoreAsync(stateChanges, ct);
            else
                Transition(SyncRuntimeState.Disabled, null, stateChanges);

            return;
        }

        if (_running)
        {
            Transition(SyncRuntimeState.Running, null, stateChanges);
            return;
        }

        if (_activeServices.Count > 0)
        {
            Transition(SyncRuntimeState.Stopping, null, stateChanges);
            await StopControlledServicesAsync(CancellationToken.None);
        }

        Transition(SyncRuntimeState.Starting, null, stateChanges);
        try
        {
            foreach (var hostedService in _controlledServices)
            {
                _activeServices.Add(hostedService);
                await hostedService.StartAsync(ct);
            }

            _running = true;
            Transition(SyncRuntimeState.Running, null, stateChanges);
        }
        catch (Exception startException)
        {
            try
            {
                await StopControlledServicesAsync(CancellationToken.None);
            }
            catch (Exception stopException)
            {
                startException = new AggregateException(startException, stopException);
            }

            _running = false;
            Transition(SyncRuntimeState.Degraded, startException, stateChanges);
            throw startException;
        }
    }

    private async Task StopCoreAsync(
        ICollection<SyncRuntimeStateChangedEventArgs> stateChanges,
        CancellationToken ct,
        bool reconcileAll = false)
    {
        if (!_running && _activeServices.Count == 0)
        {
            if (!reconcileAll)
            {
                Transition(SyncRuntimeState.Disabled, null, stateChanges);
                return;
            }

            Transition(SyncRuntimeState.Stopping, null, stateChanges);
            _activeServices.AddRange(_controlledServices);
            try
            {
                await StopControlledServicesAsync(ct);
                Transition(SyncRuntimeState.Disabled, null, stateChanges);
            }
            catch (Exception exception)
            {
                Transition(SyncRuntimeState.Degraded, exception, stateChanges);
                throw;
            }

            return;
        }

        Transition(SyncRuntimeState.Stopping, null, stateChanges);
        try
        {
            await StopControlledServicesAsync(ct);
            _running = false;
            Transition(SyncRuntimeState.Disabled, null, stateChanges);
        }
        catch (Exception exception)
        {
            _running = false;
            Transition(SyncRuntimeState.Degraded, exception, stateChanges);
            throw;
        }
    }

    private async Task StopControlledServicesAsync(CancellationToken ct)
    {
        var failures = new List<Exception>();

        try
        {
            await _deviceSyncTasks.StopAllAsync(ct);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        var failedServices = new HashSet<ISyncControlledHostedService>();
        for (var index = _activeServices.Count - 1; index >= 0; index--)
        {
            var hostedService = _activeServices[index];
            try
            {
                await hostedService.StopAsync(ct);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                failedServices.Add(hostedService);
            }
        }

        if (failedServices.Count == 0)
        {
            _activeServices.Clear();
        }
        else
        {
            _activeServices.RemoveAll(service => !failedServices.Contains(service));
        }

        _syncDeviceIdentities.Clear();
        _presenceRegistry?.InvalidateAll("synchronization runtime stopped");
        _endpointRegistry.Clear();

        if (failures.Count == 1)
            throw failures[0];

        if (failures.Count > 1)
            throw new AggregateException(failures);
    }

    private void Transition(
        SyncRuntimeState state,
        Exception? failure,
        ICollection<SyncRuntimeStateChangedEventArgs> stateChanges)
    {
        lock (_stateLock)
        {
            var next = new SyncRuntimeSnapshot(state, failure);
            if (_snapshot == next)
                return;

            var previous = _snapshot;
            _snapshot = next;
            stateChanges.Add(new SyncRuntimeStateChangedEventArgs(previous, next));
        }
    }

    private void Publish(IEnumerable<SyncRuntimeStateChangedEventArgs> stateChanges)
    {
        foreach (var stateChange in stateChanges)
            Publish(stateChange);
    }

    private void Publish(SyncRuntimeStateChangedEventArgs stateChange)
    {
        var handlers = StateChanged;
        if (handlers is null)
            return;

        foreach (EventHandler<SyncRuntimeStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, stateChange);
            }
            catch
            {
            }
        }
    }
}
