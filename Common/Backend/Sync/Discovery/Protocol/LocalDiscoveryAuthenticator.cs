using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Constants;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Backend.Sync.Discovery.Protocol;

internal static class LocalDiscoveryAuthenticator
{
    public static byte[] ComputeMac(byte[] secret, ReadOnlySpan<byte> authenticatedBytes)
    {
        if (secret.Length == 0)
            throw new ArgumentException("The discovery secret is missing.", nameof(secret));

        return HMACSHA256.HashData(secret, authenticatedBytes);
    }


    public static bool VerifyMac(byte[] secret, ReadOnlySpan<byte> authenticatedBytes, byte[] mac)
    {
        if (secret.Length == 0 || mac.Length != SyncConstants.LocalDiscoveryMacBytes)
            return false;

        var expected = ComputeMac(secret, authenticatedBytes);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, mac);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
        }
    }


    public static bool VerifySignature(byte[] signPublicKey, ReadOnlySpan<byte> authenticatedBytes, byte[] signature)
    {
        if (signPublicKey.Length != SyncConstants.SyncDeltaEd25519PublicKeyBytes ||
            signature.Length != SyncConstants.LocalDiscoverySignatureBytes)
            return false;

        try
        {
            var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, signPublicKey, KeyBlobFormat.RawPublicKey);
            return SignatureAlgorithm.Ed25519.Verify(publicKey, authenticatedBytes, signature);
        }
        catch
        {
            return false;
        }
    }


    public static bool IsFresh(long unixTimeSeconds, DateTimeOffset now)
    {
        DateTimeOffset packetTime;
        try
        {
            packetTime = DateTimeOffset.FromUnixTimeSeconds(unixTimeSeconds);
        }
        catch
        {
            return false;
        }

        return (now - packetTime).Duration() <= TimeSpan.FromSeconds(SyncConstants.LocalDiscoveryMaxClockSkewSeconds);
    }
}
