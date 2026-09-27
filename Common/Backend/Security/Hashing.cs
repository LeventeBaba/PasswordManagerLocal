using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Backend.Security;

public static class Hashing
{
    public static byte[] SHA256Hash(ReadOnlySpan<byte> data)
    {
        return SHA256.HashData(data);
    }

    public static byte[] SHA512Hash(ReadOnlySpan<byte> data)
    {
        return SHA512.HashData(data);
    }

    public static byte[] SHA256Hash(Action<Sha256HashBuilder> writeData)
    {
        using var builder = new Sha256HashBuilder();
        writeData(builder);
        return builder.GetHashAndReset();
    }

    public static byte[] SHA256Hash(ReadOnlySpan<byte> data, ReadOnlySpan<byte> salt)
    {
        var buf = new byte[data.Length + salt.Length];
        data.CopyTo(buf);
        salt.CopyTo(buf.AsSpan(data.Length));
        try
        {
            return SHA256.HashData(buf);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buf);
        }
    }

    public static byte[] SHA512Hash(ReadOnlySpan<byte> data, ReadOnlySpan<byte> salt)
    {
        var buf = new byte[data.Length + salt.Length];
        data.CopyTo(buf);
        salt.CopyTo(buf.AsSpan(data.Length));
        try
        {
            return SHA512.HashData(buf);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buf);
        }
    }

    public static byte[] HMACSHA256(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        var keyBuffer = key.ToArray();
        var dataBuffer = data.ToArray();
        try
        {
            return System.Security.Cryptography.HMACSHA256.HashData(keyBuffer, dataBuffer);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBuffer);
            CryptographicOperations.ZeroMemory(dataBuffer);
        }
    }

    public static byte[] HMACSHA512(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        var keyBuffer = key.ToArray();
        var dataBuffer = data.ToArray();
        try
        {
            return System.Security.Cryptography.HMACSHA512.HashData(keyBuffer, dataBuffer);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBuffer);
            CryptographicOperations.ZeroMemory(dataBuffer);
        }
    }

    public static bool Verify(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static byte[] GenerateSalt(int length = 32)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        var salt = new byte[length];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }
}