using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Backend.Utils;

internal static class GeneralUserDataMergeUtil
{
    public static bool Merge(
        GeneralUserData local,
        GeneralUserData incoming,
        User existingUser,
        UserSyncPayload incomingUser)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(existingUser);
        ArgumentNullException.ThrowIfNull(incomingUser);
        SyncVersionStampComparer.Validate(local.Version);
        SyncVersionStampComparer.Validate(incoming.Version);

        var comparison = SyncVersionStampComparer.Instance.Compare(local.Version, incoming.Version);
        if (comparison == 0)
        {
            var localHash = CalculateCandidateHash(local, existingUser.UsernameHash, existingUser.UsernameSalt);
            var incomingHash = CalculateCandidateHash(incoming, incomingUser.UsernameHash, incomingUser.UsernameSalt);
            if (!localHash.AsSpan().SequenceEqual(incomingHash))
            {
                throw new DeterministicSyncConflictException(
                    "general-user-data",
                    existingUser.UId,
                    local.Version,
                    localHash,
                    incomingHash);
            }

            return false;
        }

        if (comparison > 0)
            return false;

        local.Username = incoming.Username;
        local.FirstName = incoming.FirstName;
        local.LastName = incoming.LastName;
        local.Email = incoming.Email;
        local.RegistrationDate = incoming.RegistrationDate;
        local.RegistrationTimeZoneId = incoming.RegistrationTimeZoneId;
        local.RegistrationDeviceType = incoming.RegistrationDeviceType;
        local.LastUpdatedAt = incoming.LastUpdatedAt;
        local.Version = incoming.Version;

        CryptographicOperations.ZeroMemory(existingUser.UsernameHash);
        CryptographicOperations.ZeroMemory(existingUser.UsernameSalt);
        existingUser.UsernameHash = incomingUser.UsernameHash.ToArray();
        existingUser.UsernameSalt = incomingUser.UsernameSalt.ToArray();
        existingUser.SetGeneralUserDataVersion(incoming.Version);
        return true;
    }

    private static byte[] CalculateCandidateHash(GeneralUserData data, byte[] usernameHash, byte[] usernameSalt) =>
        Hashing.SHA256Hash(hash =>
        {
            hash.WriteBytes(data.CalculateIntegrityHash());
            hash.WriteBytes(usernameHash);
            hash.WriteBytes(usernameSalt);
        });
}
