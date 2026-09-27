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

internal sealed class SyncNetworkRefreshHostedService : ISyncControlledHostedService, IDisposable
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
    private string _lastNetworkSignature = string.Empty;
    private bool _started;

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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_started || !IsNetworkRuntimeActive())
            return Task.CompletedTask;

        var pollCancellation = new CancellationTokenSource();
        lock (_lock)
        {
            _lastNetworkSignature = BuildNetworkSignature();
            _pollCancellation = pollCancellation;
            _started = true;
        }

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        _pollTask = Task.Run(
            () => PollNetworkConfigurationAsync(pollCancellation.Token),
            CancellationToken.None);

        BackendDebugLog.Info("Network-change monitoring started for synchronization/enrollment.");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started)
            return;

        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;

        CancellationTokenSource? debounceCancellation;
        CancellationTokenSource? pollCancellation;
        Task? pollTask;
        lock (_lock)
        {
            _started = false;
            debounceCancellation = _debounceCancellation;
            _debounceCancellation = null;
            pollCancellation = _pollCancellation;
            _pollCancellation = null;
            pollTask = _pollTask;
            _pollTask = null;
            _lastNetworkSignature = string.Empty;
        }

        debounceCancellation?.Cancel();
        pollCancellation?.Cancel();

        if (pollTask is not null)
        {
            try
            {
                await pollTask;
            }
            catch (OperationCanceledException) when (pollCancellation?.IsCancellationRequested == true)
            {
            }
            catch
            {
            }
        }

        pollCancellation?.Dispose();
        BackendDebugLog.Info("Network-change monitoring stopped for synchronization/enrollment.");
    }

    public void Dispose()
    {
        try
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
        }

        _profileChangeSignal.Dispose();
        _refreshLock.Dispose();
    }

    private bool IsNetworkRuntimeActive() =>
        _identity.IsSyncOn || _enrollmentState.IsActive;

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        NotifyNetworkChange();

    private void OnNetworkChanged(object? sender, EventArgs e) =>
        NotifyNetworkChange();

    private void ScheduleRefresh(bool captureCurrentSignature)
    {
        if (!IsNetworkRuntimeActive())
            return;

        var currentSignature = captureCurrentSignature ? BuildNetworkSignature() : null;
        CancellationTokenSource? previousCancellation;
        CancellationTokenSource debounceCancellation;

        lock (_lock)
        {
            if (!_started)
                return;

            if (currentSignature is not null)
                _lastNetworkSignature = currentSignature;

            previousCancellation = _debounceCancellation;
            _debounceCancellation = new CancellationTokenSource();
            debounceCancellation = _debounceCancellation;
        }

        previousCancellation?.Cancel();
        _ = Task.Run(() => RefreshAfterDebounceAsync(debounceCancellation), CancellationToken.None);
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

                if (!string.Equals(_lastNetworkSignature, currentSignature, StringComparison.Ordinal))
                {
                    _lastNetworkSignature = currentSignature;
                    changed = true;
                }
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
            if (!IsNetworkRuntimeActive())
                return;

            BackendDebugLog.Info("Network configuration changed. Restarting local synchronization/enrollment networking.");

            await _deviceSyncTasks.StopAllAsync(ct);
            _presenceRegistry?.InvalidateAll("network configuration changed");
            _endpointRegistry.Clear();

            await _discovery.StopAsync(ct);
            await _tcpServer.StopAsync(ct);

            if (!IsNetworkRuntimeActive())
                return;

            await _tcpServer.StartAsync(ct);
            await _discovery.StartAsync(ct);

            lock (_lock)
            {
                if (_started)
                    _lastNetworkSignature = BuildNetworkSignature();
            }

            BackendDebugLog.Info("Local synchronization/enrollment networking was refreshed after the network change.");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private string BuildNetworkSignature()
    {
        try
        {
            var addresses = _networkAddresses.GetMulticastInterfaceAddresses()
                .Select(address => address.ToString())
                .OrderBy(address => address, StringComparer.Ordinal)
                .ToArray();

            return $"{NetworkInterface.GetIsNetworkAvailable()}:{string.Join("|", addresses)}";
        }
        catch
        {
            return "unknown";
        }
    }
}
