using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.State;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Net.NetworkInformation;
using static PasswordManagerLocal.Common.Backend.Constants.SyncConstants;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;

using PasswordManagerLocal.Common.Backend.Diagnostics;

namespace PasswordManagerLocal.Common.Backend.Services.Hosted;

internal sealed class SyncNetworkRefreshHostedService : ISyncControlledHostedService, ISyncRuntimeHealth, IDisposable
{
    private readonly IDeviceIdentityService _identity;
    private readonly IEnrollmentRuntimeState _enrollmentState;
    private readonly IDiscoveredDeviceEndpointRegistry _endpointRegistry;
    private readonly IDeviceSyncTaskService _deviceSyncTasks;
    private readonly ILocalNetworkAddressService _networkAddresses;
    private readonly TcpSyncServerHostedService _tcpServer;
    private readonly LocalDiscoveryHostedService _discovery;
    private readonly IBackendExecutionProfileProvider _executionProfileProvider;
    private readonly BackendExecutionProfileChangeSignal _profileChangeSignal;
    private readonly IDevicePresenceRegistry? _presenceRegistry;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CancellationTokenSource? _debounceCancellation;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private NetworkSignatureChangeTracker? _networkSignatureTracker;
    private bool _started;
    private bool _passiveOwner;
    private Exception? _refreshFailure;
    public bool RequiresProcessRestart { get; private set; }
    public bool IsHealthy
    {
        get
        {
            lock (_lock)
            {
                _ = _pollTask?.Exception;
                return _started && _refreshFailure is null &&
                    ((!_identity.IsSyncOn && !_enrollmentState.IsActive) || _pollTask is { IsCompleted: false });
            }
        }
    }

    public SyncNetworkRefreshHostedService(
        IDeviceIdentityService identity,
        IEnrollmentRuntimeState enrollmentState,
        IDiscoveredDeviceEndpointRegistry endpointRegistry,
        IDeviceSyncTaskService deviceSyncTasks,
        ILocalNetworkAddressService networkAddresses,
        TcpSyncServerHostedService tcpServer,
        LocalDiscoveryHostedService discovery,
        IBackendExecutionProfileProvider executionProfileProvider,
        IDevicePresenceRegistry? presenceRegistry = null)
    {
        _identity = identity;
        _enrollmentState = enrollmentState;
        _endpointRegistry = endpointRegistry;
        _deviceSyncTasks = deviceSyncTasks;
        _networkAddresses = networkAddresses;
        _tcpServer = tcpServer;
        _discovery = discovery;
        _executionProfileProvider = executionProfileProvider
            ?? throw new ArgumentNullException(nameof(executionProfileProvider));
        _profileChangeSignal = new BackendExecutionProfileChangeSignal(_executionProfileProvider);
        _presenceRegistry = presenceRegistry;
    }

    public int StartOrder => 40;

    internal bool HasPendingRefresh
    {
        get
        {
            lock (_lock)
                return _debounceCancellation is not null;
        }
    }

    internal void NotifyNetworkChange() =>
        ScheduleRefresh(captureCurrentSignature: true);

    internal Task StartPassiveAsync(CancellationToken ct)
    {
        _passiveOwner = true;
        try { return StartAsync(ct); } catch { _passiveOwner = false; throw; }
    }

    internal async Task StopPassiveAsync(CancellationToken ct)
    {
        _passiveOwner = false;
        await StopAsync(ct);
        // A debounced refresh may already be rebinding. Drain it before its backend
        // owner closes UDP/TCP, so no late refresh can reopen a stopped listener.
        await _refreshLock.WaitAsync(ct);
        _refreshLock.Release();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RequiresProcessRestart) throw new InvalidOperationException("Network refresh requires a complete process restart.");
        if (!IsNetworkRuntimeActive()) return Task.CompletedTask;
        lock (_lock)
        {
            if (!_started)
            {
                _refreshFailure = null;
                _networkSignatureTracker = new NetworkSignatureChangeTracker(
                    BuildNetworkSignature(), NetworkInterface.GetIsNetworkAvailable());
                _started = true;
                NetworkChange.NetworkAddressChanged += OnNetworkChanged;
                NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            }
            if (_pollCancellation is null && (_identity.IsSyncOn || _enrollmentState.IsActive))
            {
                var pollCancellation = new CancellationTokenSource();
                _pollCancellation = pollCancellation;
                _pollTask = IndependentBackgroundWork.Run(
                    () => PollNetworkConfigurationAsync(pollCancellation.Token), CancellationToken.None);
            }
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started)
            return;

        if (!_passiveOwner)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        }

        CancellationTokenSource? debounceCancellation;
        CancellationTokenSource? pollCancellation;
        Task? pollTask;
        lock (_lock)
        {
            _started = _passiveOwner;
            debounceCancellation = _passiveOwner ? null : _debounceCancellation;
            if (!_passiveOwner) _debounceCancellation = null;
            pollCancellation = _pollCancellation;
            _pollCancellation = null;
            pollTask = _pollTask;
            _pollTask = null;
            if (!_passiveOwner) _networkSignatureTracker = null;
        }

        debounceCancellation?.Cancel();
        pollCancellation?.Cancel();

        if (pollTask is not null)
        {
            try
            {
                await pollTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch when (pollTask.IsCompleted) { }
            catch { RequiresProcessRestart = true; throw; }
        }

        pollCancellation?.Dispose();
        BackendDebugLog.Info("Network-change monitoring stopped for synchronization/enrollment.");
    }

    public void Dispose()
    {
        try
        {
            StopPassiveAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
        }

        _profileChangeSignal.Dispose();
        _refreshLock.Dispose();
    }

    private bool IsNetworkRuntimeActive() =>
        _passiveOwner || _identity.IsSyncOn || _enrollmentState.IsActive;

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        ScheduleRefresh(captureCurrentSignature: true, reportedAvailability: e.IsAvailable);

    private void OnNetworkChanged(object? sender, EventArgs e) =>
        NotifyNetworkChange();

    private void ScheduleRefresh(bool captureCurrentSignature, bool? reportedAvailability = null)
    {
        if (!IsNetworkRuntimeActive() || RequiresProcessRestart)
            return;

        var currentSignature = captureCurrentSignature ? BuildNetworkSignature() : null;
        CancellationTokenSource? previousCancellation;
        CancellationTokenSource debounceCancellation;

        lock (_lock)
        {
            if (!_started)
                return;

            if (currentSignature is not null)
                _networkSignatureTracker?.Observe(
                    currentSignature, isNetworkEvent: true, reportedAvailability: reportedAvailability);

            previousCancellation = _debounceCancellation;
            _debounceCancellation = new CancellationTokenSource();
            debounceCancellation = _debounceCancellation;
        }

        previousCancellation?.Cancel();
        _ = PasswordManagerLocal.Common.Backend.Utils.IndependentBackgroundWork.Run(() => RefreshAfterDebounceAsync(debounceCancellation), CancellationToken.None);
    }

    private async Task PollNetworkConfigurationAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var version = _profileChangeSignal.Version;
                var profile = _executionProfileProvider.Current;
                if (profile is null)
                    return;

                await _profileChangeSignal.WaitAsync(
                    profile.NetworkConfigurationPollInterval,
                    version,
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            if (!IsNetworkRuntimeActive())
                return;

            var currentSignature = BuildNetworkSignature();
            var changed = false;

            lock (_lock)
            {
                if (!_started)
                    return;

                // Compare against the last *successfully applied* configuration.
                // Merely observing a change must not update that baseline: doing
                // so would suppress retries if the actual restart fails.
                changed = _networkSignatureTracker?.Observe(currentSignature) == true ||
                    _networkSignatureTracker?.HasUnappliedTransition == true;
            }

            if (changed)
            {
                BackendDebugLog.Info("A network-interface change was detected by the synchronization fallback poller.");
                ScheduleRefresh(captureCurrentSignature: false);
            }
        }
    }

    private async Task RefreshAfterDebounceAsync(CancellationTokenSource debounceCancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(NetworkRefreshDebounceSeconds), debounceCancellation.Token);

            lock (_lock)
            {
                if (!_started || !ReferenceEquals(_debounceCancellation, debounceCancellation))
                    return;

                _debounceCancellation = null;
            }

            if (!IsNetworkRuntimeActive())
                return;

            await RefreshAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            lock (_lock) _refreshFailure = ex;
            BackendDebugLog.Error($"Network refresh failed: {ex.Message}", ex);
        }
        finally
        {
            debounceCancellation.Dispose();
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            if (!IsNetworkRuntimeActive() || RequiresProcessRestart)
                return;

            // An OS event does not necessarily change effective network
            // configuration. Decide only after debounce and while holding the
            // refresh lock so overlapping notifications cannot restart twice.
            var currentSignature = BuildNetworkSignature();
            long observedVersion;
            lock (_lock)
            {
                if (!_started || _networkSignatureTracker is null)
                    return;

                if (!_networkSignatureTracker.RequiresRefresh(currentSignature))
                {
                    BackendDebugLog.Debug(
                        "Skipping network refresh: effective network configuration is unchanged.",
                        "Sync");
                    return;
                }

                observedVersion = _networkSignatureTracker.ObservationVersion;
            }

            BackendDebugLog.Info("Network configuration changed. Restarting local synchronization/enrollment networking.");

            try
            {
                await _deviceSyncTasks.StopAllAsync(ct);
                _presenceRegistry?.InvalidateAll("network configuration changed");
                _endpointRegistry.Clear();
                await _discovery.StopForNetworkRefreshAsync(ct);
                await _tcpServer.StopForNetworkRefreshAsync(ct);
            }
            catch
            {
                RequiresProcessRestart = true;
                throw;
            }

            if (!IsNetworkRuntimeActive())
                return;

            await _tcpServer.StartAsync(ct);
            await _discovery.StartAsync(ct);

            var refreshedSignature = BuildNetworkSignature();
            var refreshedAvailability = NetworkInterface.GetIsNetworkAvailable();
            lock (_lock)
            {
                _refreshFailure = null;
                if (_started)
                    _networkSignatureTracker?.MarkRefreshed(
                        refreshedSignature, refreshedAvailability, observedVersion);
            }

            BackendDebugLog.Info("Local synchronization/enrollment networking was refreshed after the network change.");
        }
        finally
        {
            try
            {
                if (!RequiresProcessRestart && !_tcpServer.RequiresProcessRestart && !_discovery.RequiresProcessRestart)
                {
                    await _tcpServer.RestorePassiveAsync(CancellationToken.None);
                    await _discovery.RestorePassiveAsync(CancellationToken.None);
                }
            }
            finally { _refreshLock.Release(); }
        }
    }

    private string BuildNetworkSignature()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(networkInterface =>
                    networkInterface.OperationalStatus == OperationalStatus.Up ||
                    (OperatingSystem.IsAndroid() && networkInterface.OperationalStatus == OperationalStatus.Unknown))
                .Where(networkInterface => networkInterface.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                .Select(networkInterface =>
                {
                    var properties = networkInterface.GetIPProperties();
                    var addresses = properties.UnicastAddresses
                        .Select(addressInfo => addressInfo.Address.ToString())
                        .OrderBy(address => address, StringComparer.Ordinal);
                    var ipv4Networks = properties.UnicastAddresses
                        .Where(addressInfo => addressInfo.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        .Select(addressInfo => $"{addressInfo.Address}/{addressInfo.IPv4Mask}")
                        .OrderBy(network => network, StringComparer.Ordinal);
                    var gateways = properties.GatewayAddresses
                        .Select(gateway => gateway.Address.ToString())
                        .OrderBy(address => address, StringComparer.Ordinal);

                    return $"{networkInterface.Id}:{networkInterface.NetworkInterfaceType}:{string.Join(",", addresses)}:{string.Join(",", ipv4Networks)}:{string.Join(",", gateways)}";
                })
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();

            var multicastAddresses = _networkAddresses.GetMulticastInterfaceAddresses()
                .Select(address => address.ToString())
                .OrderBy(address => address, StringComparer.Ordinal);

            return $"{NetworkInterface.GetIsNetworkAvailable()}:{string.Join("|", interfaces)}:{string.Join("|", multicastAddresses)}";
        }
        catch
        {
            return "unknown";
        }
    }
}
