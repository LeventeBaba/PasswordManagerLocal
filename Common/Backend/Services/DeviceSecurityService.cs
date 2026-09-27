using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class DeviceSecurityService : IDeviceSecurityService
{
    private readonly IDeviceRepository _devices;
    private readonly ISyncChangeQueueService _syncQueue;
    private readonly ISyncDeviceIdentityService _syncDeviceIdentities;
    private readonly IUnitOfWork _uow;

    public DeviceSecurityService(
        IDeviceRepository devices,
        ISyncChangeQueueService syncQueue,
        ISyncDeviceIdentityService syncDeviceIdentities,
        IUnitOfWork uow)
    {
        _devices = devices;
        _syncQueue = syncQueue;
        _syncDeviceIdentities = syncDeviceIdentities;
        _uow = uow;
    }




    public async Task RecordInvalidIncomingSyncAsync(Device device, string reason, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        device.InvalidSyncAttemptCount++;
        device.LastInvalidSyncAttemptAt = now;

        if (device.InvalidSyncAttemptCount >= SyncConstants.MaxInvalidIncomingSyncAttempts)
        {
            device.IsBlocked = true;
            device.BlockedAt = now;
            device.BlockedReason = NormalizeReason(reason);
            device.LastModifiedAt = now;
            device.GenerateIntegrityHash();
            _devices.Update(device);
            _syncDeviceIdentities.TryRemove(device);

            await _syncQueue.EnqueueAsync(new SyncItem
            {
                ModelId = device.Id,
                ModelType = SyncModelType.Device,
                ChangeType = SyncChangeType.Updated
            }, ct);

            return;
        }

        device.GenerateIntegrityHash();
        _devices.Update(device);
        await _uow.SaveChangesAsync(ct);
    }


    public async Task ResetInvalidIncomingSyncAsync(Device device, CancellationToken ct = default)
    {
        if (!await _devices.ExistsAsync(device.Id, ct))
            return;

        if (device.InvalidSyncAttemptCount == 0 && device.LastInvalidSyncAttemptAt is null)
            return;

        device.InvalidSyncAttemptCount = 0;
        device.LastInvalidSyncAttemptAt = null;
        device.GenerateIntegrityHash();
        _devices.Update(device);
        await _uow.SaveChangesAsync(ct);
    }


    private string NormalizeReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "Invalid incoming sync data.";

        var trimmed = reason.Trim();
        return trimmed.Length <= 512 ? trimmed : trimmed[..512];
    }
}
