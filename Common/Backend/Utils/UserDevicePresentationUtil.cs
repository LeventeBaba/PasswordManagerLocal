using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using static PasswordManagerLocal.Common.Contracts.Constants.DataLengthConstants;

namespace PasswordManagerLocal.Common.Backend.Utils;

/// <summary>
/// Resolves concurrent duplicate encrypted device names as a deterministic read-time view.
/// Stored items and their mutation versions are never rewritten during merge.
/// </summary>
internal static class UserDevicePresentationUtil
{
    public static IReadOnlyDictionary<Guid, string> ResolveNames(IEnumerable<UserDeviceData> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var result = new Dictionary<Guid, string>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices
                     .OrderByDescending(item => item.Version, SyncVersionStampComparer.Instance)
                     .ThenBy(item => item.Id))
        {
            SyncVersionStampComparer.Validate(device.Version);
            if (device.Id == Guid.Empty)
                throw new InvalidDataException("Cannot resolve a presentation name for an empty device identifier.");

            var baseName = NormalizeBaseName(device);
            var resolved = BuildUniqueName(baseName, device.Id, usedNames);
            result.Add(device.Id, resolved);
            usedNames.Add(resolved);
        }

        return result;
    }

    private static string NormalizeBaseName(UserDeviceData device)
    {
        var name = string.IsNullOrWhiteSpace(device.Name)
            ? DeviceNameUtil.BuildDefaultDeviceName(device.Id)
            : device.Name.Trim();
        return name[..Math.Min(name.Length, UserDeviceNameMaxLength)];
    }

    private static string BuildUniqueName(string baseName, Guid deviceId, HashSet<string> usedNames)
    {
        if (!usedNames.Contains(baseName))
            return baseName;

        var idSuffix = deviceId.ToString("N")[..6];
        // There are usedNames.Count occupied values, so usedNames.Count + 1
        // deterministic candidates guarantees that at least one is free.
        for (var counter = 1; counter <= usedNames.Count + 1; counter++)
        {
            var suffix = counter == 1 ? $"-{idSuffix}" : $"-{idSuffix}-{counter}";
            var prefixLength = Math.Max(1, UserDeviceNameMaxLength - suffix.Length);
            var candidate = baseName[..Math.Min(baseName.Length, prefixLength)] + suffix;
            if (!usedNames.Contains(candidate))
                return candidate;
        }

        throw new InvalidDataException("A deterministic device presentation name could not be allocated.");
    }
}
