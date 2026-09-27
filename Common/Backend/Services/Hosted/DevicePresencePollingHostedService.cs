using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Diagnostics;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Services.Hosted;

internal sealed class DevicePresencePollingHostedService : IInteractiveBackendHostedService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBackendExecutionProfileProvider _executionProfileProvider;
    private readonly IDevicePresenceProbeService _probeService;
    private readonly IDevicePresenceRegistry _presenceRegistry;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private Task? _loopTask;

    public DevicePresencePollingHostedService(
        IServiceScopeFactory scopeFactory,
        IBackendExecutionProfileProvider executionProfileProvider,
        IDevicePresenceProbeService probeService,
        IDevicePresenceRegistry presenceRegistry)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _executionProfileProvider = executionProfileProvider ?? throw new ArgumentNullException(nameof(executionProfileProvider));
        _probeService = probeService ?? throw new ArgumentNullException(nameof(probeService));
        _presenceRegistry = presenceRegistry ?? throw new ArgumentNullException(nameof(presenceRegistry));
    }

    internal bool IsRunning => Volatile.Read(ref _loopTask) is not null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_loopTask is not null)
                return;

            var lifetime = new CancellationTokenSource();
            _cancellation = lifetime;
            _loopTask = Task.Run(() => RunAsync(lifetime.Token), CancellationToken.None);
            BackendDebugLog.Debug("Interactive authenticated presence polling started.", "Presence");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            var lifetime = _cancellation;
            var task = _loopTask;
            _cancellation = null;
            _loopTask = null;

            if (lifetime is null)
                return;

            lifetime.Cancel();
            if (task is not null)
            {
                try
                {
                    await task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }
            }

            lifetime.Dispose();
            BackendDebugLog.Debug("Interactive authenticated presence polling stopped.", "Presence");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        var profile = _executionProfileProvider.Current;
        if (profile is null || !_executionProfileProvider.IsInteractive)
        {
            BackendDebugLog.DebugRateLimited(
                "presence-poll-skipped-noninteractive",
                TimeSpan.FromMinutes(1),
                "Authenticated presence polling was skipped because there is no interactive backend session.",
                "Presence");
            return;
        }

        _presenceRegistry.ExpireStale(profile.DeviceOnlineTimeout);
        var devices = await LoadEligibleDevicesAsync(cancellationToken);
        if (devices.Count == 0)
            return;

        await Task.WhenAll(devices.Select(device =>
            _probeService.ProbeAsync(device, force: false, cancellationToken: cancellationToken)));
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

        _lifecycleLock.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                BackendDebugLog.DebugRateLimited(
                    "presence-poll-loop-failure",
                    TimeSpan.FromSeconds(15),
                    $"Interactive authenticated presence polling iteration failed. ErrorType={exception.GetType().Name}.",
                    "Presence");
            }

            var profile = _executionProfileProvider.Current;
            if (profile is null || !_executionProfileProvider.IsInteractive)
                return;

            var baseDelay = profile.LocalDiscoveryInterval <= TimeSpan.Zero
                ? TimeSpan.FromSeconds(15)
                : profile.LocalDiscoveryInterval;
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1500));
            try
            {
                await Task.Delay(baseDelay + jitter, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<IReadOnlyList<Device>> LoadEligibleDevicesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var localUsers = scope.ServiceProvider.GetRequiredService<ILocalUserDeviceRepository>();
        var enabledUserIds = await localUsers.ListSyncOnUserIdsAsync(cancellationToken);
        if (enabledUserIds.Count == 0)
            return [];

        var links = await scope.ServiceProvider
            .GetRequiredService<IUserDeviceRepository>()
            .ListByUsersWithDevicesAsync(enabledUserIds, cancellationToken);

        return links
            .Where(link => !link.IsDeleted && link.IsSyncOn && CanProbe(link.Device))
            .Select(link => link.Device!)
            .GroupBy(device => device.Id)
            .Select(group => group.First())
            .ToArray();
    }

    private static bool CanProbe(Device? device) =>
        device is not null &&
        device.Id != Guid.Empty &&
        device.IsTrusted &&
        !device.IsBlocked &&
        device.PublicKey.Length != 0 &&
        device.SignPublicKey.Length != 0 &&
        !string.IsNullOrWhiteSpace(device.TlsCertFingerprint);
}
