using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Utils;

public static class DeviceTypeDetector
{
    public static DeviceType Detect()
    {
        if (OperatingSystem.IsWindows())
            return DeviceType.WindowsPc;

        if (OperatingSystem.IsAndroid())
            return DeviceType.AndroidMobile;

        return DeviceType.Unknown;
    }

    public static bool IsValid(DeviceType deviceType) =>
        deviceType is DeviceType.WindowsPc or DeviceType.AndroidMobile;
}
