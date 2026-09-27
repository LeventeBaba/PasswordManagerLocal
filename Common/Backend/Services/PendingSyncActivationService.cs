using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Publishes pending remote-device identities for discovery and starts delivery through registered endpoints.
/// </summary>
public sealed class PendingSyncActivationService : IPendingSyncActivationService
{
    private readonly IDeviceRepository _devices;
    private readonly ISyncDeviceIdentityService _syncDeviceIdentities;
    private readonly IDiscoveredDeviceEndpointRegistry _endpointRegistry;
    private readonly IDeviceSyncTaskService _deviceSyncTasks;
    private readonly IDeviceIdentityService _identity;
    private readonly ILocalDeviceMatcherService _localDevices;

    public PendingSyncActivationService(
        IDeviceRepository devices,
        ISyncDeviceIdentityService syncDeviceIdentities,
        IDiscoveredDeviceEndpointRegistry endpointRegistry,
        IDeviceSyncTaskService deviceSyncTasks,
        IDeviceIdentityService identity,
        ILocalDeviceMatcherService localDevices)
    {
        _devices = devices;
        _syncDeviceIdentities = syncDeviceIdentities;
        _endpointRegistry = endpointRegistry;
        _deviceSyncTasks = deviceSyncTasks;
        _identity = identity;
        _localDevices = localDevices;
    }

    public async Task ActivatePendingAsync(CancellationToken ct = default)
    {
        var pendingDevices = await _devices.ListDevicesNeedingSyncAsync(ct);
        ActivateDevices(pendingDevices);
    }

    public void ActivateDevices(IReadOnlyList<Device> devices)
    {
        if (devices.Count == 0)
            return;

        if (!_identity.IsSyncOn)
        {
            foreach (var device in devices)
                RemoveDiscoveryState(device);

            return;
        }

        foreach (var device in devices)
        {
            if (CanActivate(device))
            {
                _syncDeviceIdentities.TryAdd(device);
                TryStartCachedEndpointSync(device);
            }
            else
            {
                RemoveDiscoveryState(device);
            }
        }
    }

    private void TryStartCachedEndpointSync(Device device)
    {
        if (!_identity.IsSyncOn)
            return;

        if (!_endpointRegistry.TryGetByFingerprint(device.TlsCertFingerprint, out var endpoint) || endpoint is null)
            return;

        _deviceSyncTasks.TryStart(endpoint, device);
    }

    private bool CanActivate(Device device) =>
        device.IsTrusted &&
        !device.IsBlocked &&
        device.PublicKey.Length != 0 &&
        device.SignPublicKey.Length != 0 &&
        !string.IsNullOrWhiteSpace(device.TlsCertFingerprint) &&
        !_localDevices.IsLocalDevice(device);

    private void RemoveDiscoveryState(Device device)
    {
        _syncDeviceIdentities.TryRemove(device);
        _endpointRegistry.TryRemove(device.TlsCertFingerprint);
    }
}
