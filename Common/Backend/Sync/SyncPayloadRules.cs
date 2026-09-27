using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Sync;

public static class SyncPayloadRules
{
    public static bool DeletesSourceDevice(SyncDeltaPayload payload, Device sourceDevice) =>
        payload.ModelType == SyncModelType.Device &&
        payload.ChangeType == SyncChangeType.Deleted &&
        payload.ModelId == sourceDevice.Id;

    public static bool IsRemoteUserDeviceDeletion(SyncDeltaPayload payload, IDeviceIdentityService identity) =>
        payload.ModelType == SyncModelType.UserDevice &&
        payload.ChangeType == SyncChangeType.Deleted &&
        payload.UserDevice is not null &&
        payload.UserDevice.IsDeleted &&
        payload.UserDevice.DeviceId != identity.LocalDeviceId;

    public static bool DeletesLocalUserProfile(SyncDeltaPayload payload, IDeviceIdentityService identity) =>
        payload.ModelType == SyncModelType.UserDevice &&
        payload.ChangeType == SyncChangeType.Deleted &&
        payload.UserDevice is not null &&
        payload.UserDevice.IsDeleted &&
        payload.UserDevice.DeviceId == identity.LocalDeviceId;

    public static bool IsLocalDevicePayload(SyncDeltaPayload payload, IDeviceIdentityService identity) =>
        payload.ModelType == SyncModelType.Device &&
        (payload.ModelId == identity.LocalDeviceId || IsLocalDevicePayload(payload.Device, identity));

    public static bool IsLocalDevicePayload(DeviceSyncPayload? device, IDeviceIdentityService identity) =>
        device is not null &&
        (device.Id == identity.LocalDeviceId ||
         device.SignPublicKey.SequenceEqual(identity.SignPublicKey) ||
         string.Equals(
             FingerprintUtil.NormalizeOrEmpty(device.TlsCertFingerprint),
             FingerprintUtil.NormalizeOrEmpty(identity.FingerprintHex),
             StringComparison.OrdinalIgnoreCase));

    public static bool ShouldPropagate(SyncDeltaPayload payload, IDeviceIdentityService identity) =>
        !DeletesLocalUserProfile(payload, identity) &&
        !IsLocalDevicePayload(payload, identity);

    public static void ValidateUserDevicePayload(SyncDeltaPayload payload)
    {
        if (payload.UserDevice is null)
            throw new InvalidDataException("User device sync payload is missing.");

        if (payload.UserDevice.UserId == Guid.Empty || payload.UserDevice.DeviceId == Guid.Empty)
            throw new InvalidDataException("User device sync payload contains an invalid id.");

        var expectedModelId = SyncIdentityUtil.BuildUserDeviceModelId(payload.UserDevice.UserId, payload.UserDevice.DeviceId);
        if (payload.ModelId != expectedModelId)
            throw new InvalidDataException("User device sync model id is invalid.");

        if (payload.ChangeType == SyncChangeType.Deleted && !payload.UserDevice.IsDeleted)
            throw new InvalidDataException("Deleted user-device delta must contain a deleted link payload.");

        if (payload.ChangeType == SyncChangeType.Deleted && payload.UserDevice.IsSyncOn)
            throw new InvalidDataException("Deleted user-device delta cannot keep synchronization enabled.");

        if (payload.ChangeType == SyncChangeType.Deleted && payload.UserDevice.DeletedAt is null)
            throw new InvalidDataException("Deleted user-device delta must contain deletion time.");

        if (payload.UserDevice.IntegrityHash.Length != CryptographyConstants.Sha256HashSizeInBytes)
            throw new InvalidDataException("User device sync hash is missing.");
    }
}
