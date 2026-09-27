using System.Security.Cryptography;
using System.Text;

namespace PasswordManagerLocal.Common.Frontend.Helpers;

internal static class SecretTransform
{
    public static byte[] HashPassword(string password)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);

        try
        {
            return SHA512.HashData(passwordBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public static byte[] Utf8Bytes(string value) => Encoding.UTF8.GetBytes(value);
}
