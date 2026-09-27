using PasswordManagerLocal.Common.Contracts.Security;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Contracts.Errors;

public sealed class KeyProtectorUnavailableException : CryptographicException
{
    public KeyProtectorUnavailableException(
        KeyProtectorUnavailableReason reason,
        Exception? innerException = null)
        : base(BuildMessage(reason), innerException)
    {
        Reason = reason;
    }

    public KeyProtectorUnavailableReason Reason { get; }

    private static string BuildMessage(KeyProtectorUnavailableReason reason) => reason switch
    {
        KeyProtectorUnavailableReason.DeviceLocked =>
            "The platform key is temporarily unavailable because the device is locked.",
        KeyProtectorUnavailableReason.PlatformKeyStoreUnavailable =>
            "The platform key store is temporarily unavailable.",
        _ => "The platform key is temporarily unavailable."
    };
}
