using PasswordManagerLocal.Common.Backend.Models.Encrypted;

namespace PasswordManagerLocal.Common.Backend.Utils;

public static class UserDeviceLoginUtil
{
    public static void UpdateCurrentDeviceLastLoginDate(
        UserDevicesData userDevicesData,
        Guid localDeviceId,
        DateTimeOffset loginTime,
        SyncVersionStamp loginVersion)
    {
        var device = userDevicesData.Devices.FirstOrDefault(device => device.Id == localDeviceId);
        if (device is null)
        {
            device = new UserDeviceData
            {
                Id = localDeviceId,
                Name = DeviceNameUtil.BuildDefaultDeviceName(localDeviceId),
                LinkedAt = loginTime,
                LastUpdatedAt = loginTime,
                Version = loginVersion
            };
            userDevicesData.Devices.Add(device);
        }

        userDevicesData.DeletedDevices.RemoveAll(deleted => deleted.Id == localDeviceId);
        device.PreviousLoginDate = IsMeaningfulLoginDate(device.LastLoginDate)
            ? UtcDateTimeUtil.ToUtc(device.LastLoginDate)
            : null;
        device.LastLoginDate = loginTime.UtcDateTime;
        device.LastUpdatedAt = loginTime;
        device.Version = loginVersion;
        device.GenerateIntegrityHash();
    }

    private static bool IsMeaningfulLoginDate(DateTime value) =>
        value != default && value != UtcDateTimeUtil.MinDateTime;
}
