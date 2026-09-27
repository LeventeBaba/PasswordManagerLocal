using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Diagnostics;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Collections.Concurrent;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class DevicePresenceProbeService : IDevicePresenceProbeService, IDisposable
{
    private static readonly TimeSpan MinimumProbeInterval = TimeSpan.FromSeconds(2);

    private readonly IDeviceIdentityService _identity;
    private readonly IBackendExecutionProfileProvider _executionProfileProvider;
    private readonly IDiscoveredDeviceEndpointRegistry _endpointRegistry;
    private readonly IDevicePresenceRegistry _presenceRegistry;
    private readonly ISyncTransportClientService _transport;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _probeLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastProbeStarted = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public DevicePresenceProbeService(
        IDeviceIdentityService identity,
        IBackendExecutionProfileProvider executionProfileProvider,
        IDiscoveredDeviceEndpointRegistry endpointRegistry,
        IDevicePresenceRegistry presenceRegistry,
        ISyncTransportClientService transport,
        IServiceScopeFactory scopeFactory)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _executionProfileProvider = executionProfileProvider ?? throw new ArgumentNullException(nameof(executionProfileProvider));
        _endpointRegistry = endpointRegistry ?? throw new ArgumentNullException(nameof(endpointRegistry));
        _presenceRegistry = presenceRegistry ?? throw new ArgumentNullException(nameof(presenceRegistry));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async Task<DevicePresenceProbeResult> ProbeAsync(
        Device device,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ThrowIfDisposed();

        var fingerprint = FingerprintUtil.NormalizeOrEmpty(device.TlsCertFingerprint);
        if (!CanProbe(device, fingerprint))
        {
            if (fingerprint.Length != 0)
                _presenceRegistry.RecordFailure(fingerprint, endpoint: null, DevicePresenceFailureKind.Unauthorized);
            return DevicePresenceProbeResult.Failed(DevicePresenceFailureKind.Unauthorized);
        }

        if (!_executionProfileProvider.IsInteractive)
        {
            BackendDebugLog.DebugRateLimited(
                "presence-poll-skipped-noninteractive",
                TimeSpan.FromMinutes(1),
                "Authenticated presence polling was skipped because there is no interactive backend session.",
                "Presence");
            return DevicePresenceProbeResult.Failed(DevicePresenceFailureKind.EndpointUnavailable);
        }

        if (!await HasEligibleRouteAsync(device.Id, cancellationToken))
        {
            _presenceRegistry.RecordFailure(fingerprint, endpoint: null, DevicePresenceFailureKind.Unauthorized);
            return DevicePresenceProbeResult.Failed(DevicePresenceFailureKind.Unauthorized);
        }

        if (!_endpointRegistry.TryGetByFingerprint(fingerprint, out var endpoint) || endpoint is null)
        {
            _presenceRegistry.RecordFailure(fingerprint, null, DevicePresenceFailureKind.EndpointUnavailable);
            return DevicePresenceProbeResult.Failed(DevicePresenceFailureKind.EndpointUnavailable);
        }

        _presenceRegistry.HandleEndpointChanged(fingerprint, endpoint);

        var gate = _probeLocks.GetOrAdd(fingerprint, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!force &&
                _lastProbeStarted.TryGetValue(fingerprint, out var lastStarted) &&
                DateTimeOffset.UtcNow - lastStarted < MinimumProbeInterval)
            {
                var profile = _executionProfileProvider.Current;
                return profile is not null && _presenceRegistry.IsOnline(fingerprint, profile.DeviceOnlineTimeout)
                    ? DevicePresenceProbeResult.Success
                    : DevicePresenceProbeResult.Failed(DevicePresenceFailureKind.EndpointUnavailable);
            }

            _lastProbeStarted[fingerprint] = DateTimeOffset.UtcNow;
            BackendDebugLog.Debug(
                $"Authenticated presence probe started. TargetDeviceId={device.Id:N}, FingerprintPrefix={Prefix(fingerprint)}, Endpoint={endpoint.Host}:{endpoint.Port}.",
                "Presence");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(SyncConstants.DevicePresenceProbeTimeoutSeconds));

            var expectedDeviceId = Convert.ToHexString(Hashing.SHA256Hash(device.SignPublicKey));
            var result = await _transport.ProbeAsync(
                endpoint.Host,
                endpoint.Port,
                fingerprint,
                expectedDeviceId,
                device.SignPublicKey,
                timeout.Token);

            if (result.IsSuccess)
            {
                _presenceRegistry.RefreshAuthenticated(
                    fingerprint,
                    endpoint,
                    DevicePresenceObservationSource.DirectProbe);
                BackendDebugLog.Debug(
                    $"Authenticated presence probe succeeded. TargetDeviceId={device.Id:N}, FingerprintPrefix={Prefix(fingerprint)}, Endpoint={endpoint.Host}:{endpoint.Port}.",
                    "Presence");
                return result;
            }

            _presenceRegistry.RecordFailure(fingerprint, endpoint, result.FailureKind);
            BackendDebugLog.DebugRateLimited(
                $"presence-probe-failed:{fingerprint}:{result.FailureKind}",
                TimeSpan.FromSeconds(5),
                $"Authenticated presence probe failed. TargetDeviceId={device.Id:N}, FingerprintPrefix={Prefix(fingerprint)}, Failure={result.FailureKind}, Endpoint={endpoint.Host}:{endpoint.Port}.",
                "Presence");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var result = DevicePresenceProbeResult.Failed(DevicePresenceFailureKind.Timeout);
            _presenceRegistry.RecordFailure(fingerprint, endpoint, result.FailureKind);
            BackendDebugLog.DebugRateLimited(
                $"presence-probe-timeout:{fingerprint}",
                TimeSpan.FromSeconds(5),
                $"Authenticated presence probe timed out. TargetDeviceId={device.Id:N}, FingerprintPrefix={Prefix(fingerprint)}, Endpoint={endpoint.Host}:{endpoint.Port}.",
                "Presence");
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var result = DevicePresenceProbeResult.Failed(DevicePresenceFailureKind.InvalidResponse);
            _presenceRegistry.RecordFailure(fingerprint, endpoint, result.FailureKind);
            BackendDebugLog.DebugRateLimited(
                $"presence-probe-exception:{fingerprint}:{exception.GetType().Name}",
                TimeSpan.FromSeconds(10),
                $"Authenticated presence probe failed unexpectedly. TargetDeviceId={device.Id:N}, FingerprintPrefix={Prefix(fingerprint)}, ErrorType={exception.GetType().Name}.",
                "Presence");
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public void OnEndpointDiscovered(Device device, DiscoveredDeviceEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var fingerprint = FingerprintUtil.NormalizeOrEmpty(device.TlsCertFingerprint);
        if (fingerprint.Length == 0)
            return;

        _presenceRegistry.HandleEndpointChanged(fingerprint, endpoint);
        BackendDebugLog.Debug(
            $"Candidate synchronization endpoint was discovered or changed. TargetDeviceId={device.Id:N}, FingerprintPrefix={Prefix(fingerprint)}, Endpoint={endpoint.Host}:{endpoint.Port}.",
            "Presence");

        if (!_executionProfileProvider.IsInteractive)
        {
            BackendDebugLog.DebugRateLimited(
                "presence-endpoint-probe-skipped-noninteractive",
                TimeSpan.FromMinutes(1),
                "A newly discovered endpoint was retained for synchronization, but UI-facing presence probing was skipped because there is no interactive session.",
                "Presence");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ProbeAsync(device, force: true, cancellationToken: CancellationToken.None);
            }
            catch (Exception exception)
            {
                BackendDebugLog.DebugRateLimited(
                    $"presence-discovery-probe-exception:{fingerprint}",
                    TimeSpan.FromSeconds(10),
                    $"The authenticated probe scheduled for a discovered endpoint failed unexpectedly. TargetDeviceId={device.Id:N}, ErrorType={exception.GetType().Name}.",
                    "Presence");
            }
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var gate in _probeLocks.Values)
            gate.Dispose();
        _probeLocks.Clear();
        _lastProbeStarted.Clear();
    }

    private async Task<bool> HasEligibleRouteAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<ISyncRouteRepository>()
            .HasEligibleUserForDeviceAsync(deviceId, cancellationToken);
    }

    private bool CanProbe(Device device, string fingerprint) =>
        _identity.IsSyncOn &&
        device.Id != Guid.Empty &&
        device.Id != _identity.LocalDeviceId &&
        device.IsTrusted &&
        !device.IsBlocked &&
        device.PublicKey.Length != 0 &&
        device.SignPublicKey.Length != 0 &&
        fingerprint.Length != 0;

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(DevicePresenceProbeService));
    }

    private static string Prefix(string fingerprint) =>
        fingerprint[..Math.Min(16, fingerprint.Length)];
}
