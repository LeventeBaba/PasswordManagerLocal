using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Sync;

public static class SyncIdentityUtil
{
    public static string NormalizeFingerprint(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
            throw new InvalidDataException("The TLS certificate fingerprint is missing.");
        var normalized = fingerprint.Replace(":", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("The TLS certificate fingerprint is invalid.");
        return normalized;
    }

    public static Guid BuildUserDeviceModelId(Guid userId, Guid deviceId)
    {
        var bytes = new byte[32];
        userId.ToByteArray().CopyTo(bytes, 0);
        deviceId.ToByteArray().CopyTo(bytes, 16);

        var hash = Hashing.SHA256Hash(bytes);
        var guidBytes = hash.Take(16).ToArray();
        return new Guid(guidBytes);
    }
}
