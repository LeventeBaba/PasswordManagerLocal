using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Sync;

public static class UserCanonicalCheckpointUtil
{
    private const string ContentDomain = "PasswordManagerLocal.Common.Backend.UserCanonicalCheckpoint.Content.v1";
    private const string SignatureDomain = "PasswordManagerLocal.Common.Backend.UserCanonicalCheckpoint.Signature.v1";

    public static byte[] CalculateCanonicalContentHash(User user) =>
        Hashing.SHA256Hash(hash =>
        {
            hash.WriteString(ContentDomain);
            hash.Write(user.UId);
            hash.WriteBytes(user.UsernameHash);
            hash.WriteBytes(user.UsernameSalt);
            hash.WriteBytes(user.PasswordSalt);
            hash.WriteBytes(user.EncryptedPayload);
            hash.WriteBytes(user.EncryptedGeneralUserDataPayload);
            hash.WriteBytes(user.EncryptedUserPasswordsDataPayload);
            hash.WriteBytes(user.EncryptedUserDevicesDataPayload);
            hash.Write(user.KeyEpoch);
            hash.Write(user.MembershipEpoch);
            user.GetGeneralUserDataVersion().WriteTo(hash);
            hash.Write(user.LastModifiedAt);
            hash.Write(user.UserDataLastModifiedAt);
            hash.Write(user.GeneralUserDataLastModifiedAt);
            hash.Write(user.UserPasswordsDataLastModifiedAt);
            hash.Write(user.UserDevicesDataLastModifiedAt);
            hash.WriteBytes(user.IntegrityHash);
        });

    public static UserCanonicalCheckpoint Create(
        User user,
        long sequence,
        DateTimeOffset createdAtUtc,
        IDeviceIdentityService identity)
    {
        if (sequence <= 0)
            throw new InvalidOperationException("The canonical checkpoint sequence is invalid.");
        user.VerifyIntegrity();

        var checkpoint = new UserCanonicalCheckpoint
        {
            UserId = user.UId,
            CheckpointSequence = sequence,
            LocalDeviceId = identity.LocalDeviceId,
            LocalOriginInstanceId = identity.OriginInstanceId,
            KeyEpoch = user.KeyEpoch,
            MembershipEpoch = user.MembershipEpoch,
            CanonicalContentHash = CalculateCanonicalContentHash(user),
            UserIntegrityHash = user.IntegrityHash.ToArray(),
            SignPublicKey = identity.SignPublicKey.ToArray(),
            CreatedAtUtc = createdAtUtc.ToUniversalTime()
        };
        checkpoint.Signature = identity.Sign(BuildSignatureBytes(checkpoint));
        return checkpoint;
    }


    public static void VerifyAuthenticity(
        UserCanonicalCheckpoint checkpoint,
        Guid expectedUserId,
        IDeviceIdentityService identity)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.UserId != expectedUserId ||
            checkpoint.CheckpointSequence <= 0 ||
            checkpoint.LocalDeviceId != identity.LocalDeviceId ||
            checkpoint.LocalOriginInstanceId != identity.OriginInstanceId ||
            checkpoint.KeyEpoch <= 0 ||
            checkpoint.MembershipEpoch <= 0 ||
            checkpoint.CanonicalContentHash.Length != SyncConstants.SyncDeltaPayloadHashBytes ||
            checkpoint.UserIntegrityHash.Length != SyncConstants.SyncDeltaPayloadHashBytes ||
            checkpoint.SignPublicKey.Length != SyncConstants.SyncDeltaEd25519PublicKeyBytes ||
            checkpoint.Signature.Length != SyncConstants.SyncDeltaEd25519SignatureBytes ||
            checkpoint.CreatedAtUtc == default)
        {
            throw new InvalidDataException("The canonical checkpoint identity or structure is invalid.");
        }

        if (!Hashing.Verify(checkpoint.SignPublicKey, identity.SignPublicKey))
            throw new InvalidDataException("The canonical checkpoint was signed by a different local identity.");

        var publicKey = PublicKey.Import(
            SignatureAlgorithm.Ed25519,
            checkpoint.SignPublicKey,
            KeyBlobFormat.RawPublicKey);
        if (!SignatureAlgorithm.Ed25519.Verify(publicKey, BuildSignatureBytes(checkpoint), checkpoint.Signature))
            throw new InvalidDataException("The canonical checkpoint signature is invalid.");
    }

    public static void Verify(
        User user,
        UserCanonicalCheckpoint checkpoint,
        IDeviceIdentityService identity)
    {
        VerifyAuthenticity(checkpoint, user.UId, identity);
        if (checkpoint.KeyEpoch != user.KeyEpoch || checkpoint.MembershipEpoch != user.MembershipEpoch)
            throw new InvalidDataException("The canonical checkpoint epoch is invalid.");
        if (!Hashing.Verify(checkpoint.UserIntegrityHash, user.IntegrityHash))
            throw new InvalidDataException("The canonical checkpoint user-integrity commitment does not match.");

        var expectedContentHash = CalculateCanonicalContentHash(user);
        if (!Hashing.Verify(checkpoint.CanonicalContentHash, expectedContentHash))
            throw new InvalidDataException("The canonical checkpoint content commitment does not match.");
    }

    private static byte[] BuildSignatureBytes(UserCanonicalCheckpoint checkpoint) =>
        Hashing.SHA256Hash(hash =>
        {
            hash.WriteString(SignatureDomain);
            hash.Write(checkpoint.UserId);
            hash.Write(checkpoint.CheckpointSequence);
            hash.Write(checkpoint.LocalDeviceId);
            hash.Write(checkpoint.LocalOriginInstanceId);
            hash.Write(checkpoint.KeyEpoch);
            hash.Write(checkpoint.MembershipEpoch);
            hash.Write(checkpoint.CreatedAtUtc);
            hash.WriteBytes(checkpoint.CanonicalContentHash);
            hash.WriteBytes(checkpoint.UserIntegrityHash);
            hash.WriteBytes(checkpoint.SignPublicKey);
        });
}
