using System.Security.Cryptography;
using System.Text.Json;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;

namespace PasswordManagerLocal.Common.Backend.Utils;

internal static class UserDataBundleCopy
{
    public static UserDataBundle Clone(UserDataBundle source)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(source, BackendJsonSerializerContext.Default.UserDataBundle);
        try
        {
            var copy = JsonSerializer.Deserialize(bytes, BackendJsonSerializerContext.Default.UserDataBundle)
                ?? throw new InvalidDataException("Could not copy the verified user bundle.");
            copy.CanonicalGeneration = source.CanonicalGeneration?.ToArray();
            return copy;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
