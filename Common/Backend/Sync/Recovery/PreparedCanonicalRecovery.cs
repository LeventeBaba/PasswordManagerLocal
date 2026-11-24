using PasswordManagerLocal.Common.Backend.Models;
using System.Security.Cryptography;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Sync.Recovery;

/// <summary>Owns speculative encrypted output; decrypted bundles never escape preparation.</summary>
public sealed class PreparedCanonicalRecovery : IDisposable
{
    private readonly User? _source;
    private readonly byte[] _outputCommitment;
    private bool _disposed;

    internal PreparedCanonicalRecovery(User user, UserDataRecoveryReconstructionResult result, User? source = null)
    {
        User = user;
        Result = result;
        _source = source;
        _outputCommitment = result.Reconstructed ? user.CalculateIntegrityHash() : [];
    }

    public User User { get; }
    public UserDataRecoveryReconstructionResult Result { get; }

    internal bool MatchesVerifiedOutput(User user) => !_disposed && Result.Reconstructed &&
        User.UId == user.UId && Hashing.Verify(_outputCommitment, user.IntegrityHash) &&
        Hashing.Verify(_outputCommitment, user.CalculateIntegrityHash());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearSpeculativeOutput(User, _source);
    }

    internal static void ClearSpeculativeOutput(User user, User? source)
    {
        ClearOutput(user.EncryptedPayload, source?.EncryptedPayload);
        ClearOutput(user.EncryptedGeneralUserDataPayload, source?.EncryptedGeneralUserDataPayload);
        ClearOutput(user.EncryptedUserPasswordsDataPayload, source?.EncryptedUserPasswordsDataPayload);
        ClearOutput(user.EncryptedUserDevicesDataPayload, source?.EncryptedUserDevicesDataPayload);
        if (user.SavedKey is { } saved) CryptographicOperations.ZeroMemory(saved);
    }

    private static void ClearOutput(byte[] output, byte[]? input)
    {
        if (!ReferenceEquals(output, input)) CryptographicOperations.ZeroMemory(output);
    }

    internal void ApplyTo(User target)
    {
        if (!MatchesVerifiedOutput(User)) throw new InvalidDataException("Prepared recovery output is no longer verified.");
        if (target.UId != User.UId) throw new InvalidOperationException("Recovery account mismatch.");
        target.UsernameHash = User.UsernameHash; target.UsernameSalt = User.UsernameSalt;
        target.PasswordSalt = User.PasswordSalt;
        target.EncryptedPayload = User.EncryptedPayload;
        target.EncryptedGeneralUserDataPayload = User.EncryptedGeneralUserDataPayload;
        target.EncryptedUserPasswordsDataPayload = User.EncryptedUserPasswordsDataPayload;
        target.EncryptedUserDevicesDataPayload = User.EncryptedUserDevicesDataPayload;
        target.KeyEpoch = User.KeyEpoch; target.MembershipEpoch = User.MembershipEpoch;
        target.GeneralDataVersionPhysicalTimeUnixMilliseconds = User.GeneralDataVersionPhysicalTimeUnixMilliseconds;
        target.GeneralDataVersionLogicalCounter = User.GeneralDataVersionLogicalCounter;
        target.GeneralDataVersionOriginDeviceId = User.GeneralDataVersionOriginDeviceId;
        target.GeneralDataVersionOriginInstanceId = User.GeneralDataVersionOriginInstanceId;
        target.LastModifiedAt = User.LastModifiedAt; target.UserDataLastModifiedAt = User.UserDataLastModifiedAt;
        target.GeneralUserDataLastModifiedAt = User.GeneralUserDataLastModifiedAt;
        target.UserPasswordsDataLastModifiedAt = User.UserPasswordsDataLastModifiedAt;
        target.UserDevicesDataLastModifiedAt = User.UserDevicesDataLastModifiedAt;
        target.IntegrityHash = User.IntegrityHash;
        // Remembered access and relationship rows belong to the current installation.
    }

    internal static User Copy(User source) => new()
    {
        UId = source.UId,
        UsernameHash = source.UsernameHash.ToArray(), UsernameSalt = source.UsernameSalt.ToArray(),
        PasswordSalt = source.PasswordSalt.ToArray(), IntegrityHash = source.IntegrityHash.ToArray(),
        // Ciphertexts are read-only during preparation and replaced rather than edited. Sharing
        // these exclusively owned input buffers avoids duplicating the large damaged payloads.
        EncryptedPayload = source.EncryptedPayload,
        EncryptedGeneralUserDataPayload = source.EncryptedGeneralUserDataPayload,
        EncryptedUserPasswordsDataPayload = source.EncryptedUserPasswordsDataPayload,
        EncryptedUserDevicesDataPayload = source.EncryptedUserDevicesDataPayload,
        SavedKey = source.SavedKey?.ToArray(), KeyEpoch = source.KeyEpoch, MembershipEpoch = source.MembershipEpoch,
        GeneralDataVersionPhysicalTimeUnixMilliseconds = source.GeneralDataVersionPhysicalTimeUnixMilliseconds,
        GeneralDataVersionLogicalCounter = source.GeneralDataVersionLogicalCounter,
        GeneralDataVersionOriginDeviceId = source.GeneralDataVersionOriginDeviceId,
        GeneralDataVersionOriginInstanceId = source.GeneralDataVersionOriginInstanceId,
        LastModifiedAt = source.LastModifiedAt, UserDataLastModifiedAt = source.UserDataLastModifiedAt,
        GeneralUserDataLastModifiedAt = source.GeneralUserDataLastModifiedAt,
        UserPasswordsDataLastModifiedAt = source.UserPasswordsDataLastModifiedAt,
        UserDevicesDataLastModifiedAt = source.UserDevicesDataLastModifiedAt,
        UserDevices = source.UserDevices, LocalUserDevices = source.LocalUserDevices
    };
}
