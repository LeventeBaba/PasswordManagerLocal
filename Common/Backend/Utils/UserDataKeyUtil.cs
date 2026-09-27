using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Backend.Utils;

public static class UserDataKeyUtil
{
    public static byte[] GenerateBlobKey()
    {
        using var key = EncryptionKey.Create();
        return key.ExportCopy();
    }

    public static void InitializeUserDataKeys(UserData userData)
    {
        ReplaceGeneralUserDataKey(userData);
        ReplaceUserPasswordsDataKey(userData);
        ReplaceUserDevicesDataKey(userData);
    }

    public static void ReplaceUserBlobKeys(UserData userData)
    {
        ReplaceGeneralUserDataKey(userData);
        ReplaceUserPasswordsDataKey(userData);
        ReplaceUserDevicesDataKey(userData);
    }

    private static void ReplaceGeneralUserDataKey(UserData userData)
    {
        CryptographicOperations.ZeroMemory(userData.GeneralUserDataKey);
        userData.GeneralUserDataKey = GenerateBlobKey();
    }

    private static void ReplaceUserPasswordsDataKey(UserData userData)
    {
        CryptographicOperations.ZeroMemory(userData.UserPasswordsDataKey);
        userData.UserPasswordsDataKey = GenerateBlobKey();
    }

    private static void ReplaceUserDevicesDataKey(UserData userData)
    {
        CryptographicOperations.ZeroMemory(userData.UserDevicesDataKey);
        userData.UserDevicesDataKey = GenerateBlobKey();
    }
}
