using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Utils;

internal static class UserDataGeneration
{
    public static byte[] Capture(User user) => Hashing.SHA256Hash(hash =>
    {
        hash.Write(user.UId);
        hash.Write(user.KeyEpoch);
        hash.Write(user.MembershipEpoch);
        hash.WriteBytes(user.EncryptedPayload);
        hash.WriteBytes(user.EncryptedGeneralUserDataPayload);
        hash.WriteBytes(user.EncryptedUserPasswordsDataPayload);
        hash.WriteBytes(user.EncryptedUserDevicesDataPayload);
    });
}
