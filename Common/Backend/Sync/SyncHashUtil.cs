using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Sync;

public static class SyncHashUtil
{
    public static byte[] CalculateUserDeviceHash(UserDevice userDevice) =>
        userDevice.CalculateIntegrityHash();

    public static byte[] CalculateUserDeviceHash(UserDeviceSyncPayload payload, long timestamp) =>
        Hashing.SHA256Hash(hash =>
        {
            hash.Write(SyncIdentityUtil.BuildUserDeviceModelId(payload.UserId, payload.DeviceId));
            hash.Write(payload.UserId);
            hash.Write(payload.DeviceId);
            hash.Write(payload.IsSyncOn);
            hash.Write(payload.IsDeleted);
            hash.Write(payload.DeletedAt);
            hash.Write(timestamp);
        });
}
