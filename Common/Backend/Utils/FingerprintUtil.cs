namespace PasswordManagerLocal.Common.Backend.Utils;

internal static class FingerprintUtil
{
    internal static string Normalize(string fingerprint) =>
        fingerprint.Replace(":", string.Empty).Replace(" ", string.Empty).Trim().ToUpperInvariant();

    internal static string NormalizeOrEmpty(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
            return string.Empty;

        return Normalize(fingerprint);
    }
}
