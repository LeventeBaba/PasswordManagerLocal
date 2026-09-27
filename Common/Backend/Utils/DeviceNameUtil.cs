using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Utils;

public static class DeviceNameUtil
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public static string BuildDefaultDeviceName(Guid deviceId)
    {
        var hash = Hashing.SHA256Hash(deviceId.ToByteArray());
        Span<char> suffix = stackalloc char[6];

        for (var i = 0; i < suffix.Length; i++)
            suffix[i] = Alphabet[hash[i] % Alphabet.Length];

        return $"Device-{new string(suffix)}";
    }
}
