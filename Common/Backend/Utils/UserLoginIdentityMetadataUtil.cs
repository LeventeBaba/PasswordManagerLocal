using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using System.Security.Cryptography;
using System.Text;

namespace PasswordManagerLocal.Common.Backend.Utils;

/// <summary>
/// Verifies that cleartext authenticated login metadata describes the same logical
/// username mutation as decrypted GeneralUserData.
/// </summary>
public static class UserLoginIdentityMetadataUtil
{
    public static void Verify(User user, GeneralUserData generalUserData)
    {
        ArgumentNullException.ThrowIfNull(user);
        Verify(user.UsernameHash, user.UsernameSalt, user.GetGeneralUserDataVersion(), generalUserData);
    }

    public static void Verify(
        byte[] usernameHash,
        byte[] usernameSalt,
        SyncVersionStamp advertisedVersion,
        GeneralUserData decryptedGeneralData)
    {
        ArgumentNullException.ThrowIfNull(usernameHash);
        ArgumentNullException.ThrowIfNull(usernameSalt);
        ArgumentNullException.ThrowIfNull(decryptedGeneralData);

        SyncVersionStampComparer.Validate(advertisedVersion);
        SyncVersionStampComparer.Validate(decryptedGeneralData.Version);
        if (!SyncVersionStampComparer.Instance.Equals(advertisedVersion, decryptedGeneralData.Version))
            throw new InvalidDataException("Authenticated login-identity version does not match decrypted general-user-data.");
        if (usernameHash.Length != CryptographyConstants.Sha256HashSizeInBytes ||
            usernameSalt.Length != CryptographyConstants.Sha256HashSizeInBytes)
            throw new InvalidDataException("Authenticated login-identity metadata has an invalid size.");

        var usernameBytes = Encoding.UTF8.GetBytes(decryptedGeneralData.Username);
        var calculatedHash = Hashing.SHA256Hash(usernameBytes, usernameSalt);
        try
        {
            if (!Hashing.Verify(usernameHash, calculatedHash))
                throw new InvalidDataException("Authenticated login identity does not match decrypted general-user-data.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(usernameBytes);
            CryptographicOperations.ZeroMemory(calculatedHash);
        }
    }
}
