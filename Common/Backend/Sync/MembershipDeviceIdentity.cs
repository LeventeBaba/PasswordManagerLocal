using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Sync;

internal static class MembershipDeviceIdentity
{
    public static bool Matches(byte[] authorizedSignKeyHash, string authorizedFingerprint,
        byte[] currentSignKeyHash, string currentFingerprint) =>
        Hashing.Verify(authorizedSignKeyHash, currentSignKeyHash) &&
        string.Equals(
            SyncIdentityUtil.NormalizeFingerprint(authorizedFingerprint),
            SyncIdentityUtil.NormalizeFingerprint(currentFingerprint),
            StringComparison.OrdinalIgnoreCase);
}
