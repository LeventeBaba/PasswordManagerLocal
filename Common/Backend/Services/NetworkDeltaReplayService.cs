using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Projections;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Security;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using static PasswordManagerLocal.Common.Contracts.Constants.DataLengthConstants;
using System.Security.Cryptography;
using System.Text.Json;
using static PasswordManagerLocal.Common.Backend.Utils.DataCodec;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class NetworkDeltaReplayService : INetworkDeltaReplayService
{
    private readonly IGroupRepository _groups;
    private readonly IDeviceRepository _devices;
    private readonly IUserDeviceRepository _userDevices;
    private readonly ISyncTombstoneRepository _tombstones;

    public NetworkDeltaReplayService(
        IGroupRepository groups,
        IDeviceRepository devices,
        IUserDeviceRepository userDevices,
        ISyncTombstoneRepository tombstones)
    {
        _groups = groups;
        _devices = devices;
        _userDevices = userDevices;
        _tombstones = tombstones;
    }

    public async Task<bool> IsAlreadyAppliedAsync(SyncDeltaPayload payload, long ts, CancellationToken ct)
    {
        if (payload.ChangeType == SyncChangeType.Deleted &&
            payload.ModelType is SyncModelType.Group or SyncModelType.Device)
        {
            var tombstone = await _tombstones.GetAsync(payload.ModelId, payload.ModelType, ct);
            return tombstone is not null && tombstone.DeletedAtTs >= ts;
        }

        if (payload.ModelType == SyncModelType.User)
            return false; // Non-deletion user updates are handled exclusively by immutable snapshots/control operations.

        if (payload.ModelType == SyncModelType.Group)
        {
            if (payload.Group is null || payload.Group.IntegrityHash.Length != CryptographyConstants.Sha256HashSizeInBytes)
                return false;

            var existing = await _groups.GetWithUserIdsAsNoTrackingAsync(payload.ModelId, ct);
            if (existing is null)
                return false;

            var existingPayload = CreateGroupSyncPayloadForHash(existing);
            var existingHash = SyncCryptoUtil.CalculateGroupHash(existingPayload, existing.LastModifiedAt.ToUnixTimeMilliseconds());
            return existingHash.SequenceEqual(payload.Group.IntegrityHash);
        }

        if (payload.ModelType == SyncModelType.Device)
        {
            if (payload.Device is null || payload.Device.IntegrityHash.Length != CryptographyConstants.Sha256HashSizeInBytes)
                return false;

            var existing = await _devices.GetByIdWithUserDevicesAsync(payload.ModelId, ct);
            if (existing is null)
                return false;

            var existingPayload = CreateDeviceSyncPayloadForHash(existing);
            var existingHash = SyncCryptoUtil.CalculateDeviceHash(existingPayload, existing.LastModifiedAt.ToUnixTimeMilliseconds());
            return existingHash.SequenceEqual(payload.Device.IntegrityHash);
        }

        if (payload.ModelType == SyncModelType.UserDevice)
        {
            if (payload.UserDevice is null || payload.UserDevice.IntegrityHash.Length != CryptographyConstants.Sha256HashSizeInBytes)
                return false;

            var existing = await _userDevices.GetAsync(payload.UserDevice.UserId, payload.UserDevice.DeviceId, ct);
            if (existing is null)
                return false;

            existing.VerifyIntegrity();
            return existing.IntegrityHash.SequenceEqual(payload.UserDevice.IntegrityHash);
        }

        return false;
    }


    public async Task<bool> IsBlockedByNewerTombstoneAsync(SyncDeltaPayload payload, long ts, CancellationToken ct)
    {
        var tombstone = await _tombstones.GetAsync(payload.ModelId, payload.ModelType, ct);
        return tombstone is not null && tombstone.DeletedAtTs >= ts;
    }


    private GroupSyncPayload CreateGroupSyncPayloadForHash(GroupWithUserIdsData group) =>
        new()
        {
            Id = group.Id,
            EncryptedPayload = group.EncryptedPayload,
            UserIds = group.UserIds
        };


    private DeviceSyncPayload CreateDeviceSyncPayloadForHash(Device device) =>
        new()
        {
            Id = device.Id,
            PublicKey = device.PublicKey,
            SignPublicKey = device.SignPublicKey,
            TlsCertFingerprint = device.TlsCertFingerprint,
            DeviceType = device.DeviceType,
            LastKnownHash = device.LastKnownHash,
            LastSync = UtcDateTimeUtil.ToUtc(device.LastSync),
            LastSeen = UtcDateTimeUtil.ToUtc(device.LastSeen),
            IsTrusted = device.IsTrusted,
            IsBlocked = device.IsBlocked,
            BlockedReason = device.BlockedReason,
            BlockedAt = UtcDateTimeUtil.ToUtc(device.BlockedAt),
            InvalidSyncAttemptCount = device.InvalidSyncAttemptCount,
            LastInvalidSyncAttemptAt = UtcDateTimeUtil.ToUtc(device.LastInvalidSyncAttemptAt),
            UserIds = device.UserDevices.Where(ud => !ud.IsDeleted).Select(ud => ud.UserId).Distinct().ToList()
        };
}
