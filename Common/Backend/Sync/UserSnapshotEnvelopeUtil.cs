using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Sync;

public static class UserSnapshotEnvelopeUtil
{
    private const string CanonicalDomain = "PasswordManagerLocal.Common.Backend.UserSnapshot.Content.v3";

    public static void FillOriginAuthentication(UserSnapshotEnvelope envelope, IDeviceIdentityService identity)
    {
        ValidateUnsignedEnvelope(envelope);
        if (envelope.OriginDeviceId != identity.LocalDeviceId ||
            envelope.OriginInstanceId != identity.OriginInstanceId)
        {
            throw new InvalidDataException("A device can only sign snapshots for its own persisted origin identity.");
        }

        envelope.OriginSignPublicKey = identity.SignPublicKey.ToArray();
        envelope.SnapshotHash = CalculateSnapshotHash(envelope);
        envelope.OriginSignature = identity.Sign(BuildSignatureBytes(envelope));
    }

    public static void ValidateStructureAndHash(UserSnapshotEnvelope envelope)
    {
        ValidateSignedEnvelope(envelope);
        var expectedHash = CalculateSnapshotHash(envelope);
        if (!Hashing.Verify(envelope.SnapshotHash, expectedHash))
            throw new InvalidDataException("User snapshot hash is invalid.");
    }

    public static void VerifyWithSigningKey(UserSnapshotEnvelope envelope, ReadOnlySpan<byte> historicalSigningPublicKey)
    {
        ValidateStructureAndHash(envelope);
        if (!historicalSigningPublicKey.SequenceEqual(envelope.OriginSignPublicKey))
            throw new InvalidDataException("User snapshot origin signing key does not match immutable membership history.");
        var publicKey = PublicKey.Import(
            SignatureAlgorithm.Ed25519,
            historicalSigningPublicKey,
            KeyBlobFormat.RawPublicKey);
        if (!SignatureAlgorithm.Ed25519.Verify(publicKey, BuildSignatureBytes(envelope), envelope.OriginSignature))
            throw new InvalidDataException("User snapshot origin signature is invalid.");
    }

    public static byte[] CalculateSnapshotHash(UserSnapshotEnvelope envelope) =>
        Hashing.SHA256Hash(BuildCanonicalContent(envelope));

    public static byte[] BuildCanonicalContent(UserSnapshotEnvelope envelope)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(CanonicalDomain);
        writer.Write(envelope.UserId.ToByteArray());
        writer.Write(envelope.OriginDeviceId.ToByteArray());
        writer.Write(envelope.OriginInstanceId.ToByteArray());
        writer.Write(envelope.OriginRevision);
        writer.Write(envelope.UserKeyEpoch);
        writer.Write(envelope.MembershipEpoch);
        writer.Write(envelope.CreatedAtUtc.ToUniversalTime().ToUnixTimeMilliseconds());
        WriteUserPayload(writer, envelope.User);

        var coverage = envelope.Coverage
            .OrderBy(entry => entry.OriginDeviceId)
            .ThenBy(entry => entry.OriginInstanceId)
            .ThenBy(entry => entry.UserKeyEpoch)
            .ToArray();

        writer.Write(coverage.Length);
        foreach (var entry in coverage)
        {
            writer.Write(entry.OriginDeviceId.ToByteArray());
            writer.Write(entry.OriginInstanceId.ToByteArray());
            writer.Write(entry.UserKeyEpoch);
            writer.Write(entry.OriginRevision);
        }

        SyncCryptoUtil.WriteBytes(writer, envelope.OriginSignPublicKey);
        return stream.ToArray();
    }

    private static byte[] BuildSignatureBytes(UserSnapshotEnvelope envelope)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write("PasswordManagerLocal.Common.Backend.UserSnapshot.Signature.v3");
        writer.Write(envelope.UserId.ToByteArray());
        writer.Write(envelope.OriginDeviceId.ToByteArray());
        writer.Write(envelope.OriginInstanceId.ToByteArray());
        writer.Write(envelope.OriginRevision);
        writer.Write(envelope.UserKeyEpoch);
        writer.Write(envelope.MembershipEpoch);
        SyncCryptoUtil.WriteBytes(writer, envelope.SnapshotHash);
        return stream.ToArray();
    }

    private static void WriteUserPayload(BinaryWriter writer, UserSyncPayload payload)
    {
        writer.Write(payload.UId.ToByteArray());
        SyncCryptoUtil.WriteBytes(writer, payload.UsernameHash);
        SyncCryptoUtil.WriteBytes(writer, payload.UsernameSalt);
        writer.Write(payload.GeneralUserDataVersion.PhysicalTimeUnixMilliseconds);
        writer.Write(payload.GeneralUserDataVersion.LogicalCounter);
        writer.Write(payload.GeneralUserDataVersion.OriginDeviceId.ToByteArray());
        writer.Write(payload.GeneralUserDataVersion.OriginInstanceId.ToByteArray());
        SyncCryptoUtil.WriteBytes(writer, payload.PasswordSalt);
        SyncCryptoUtil.WriteBytes(writer, payload.EncryptedPayload);
        SyncCryptoUtil.WriteBytes(writer, payload.EncryptedGeneralUserDataPayload);
        SyncCryptoUtil.WriteBytes(writer, payload.EncryptedUserPasswordsDataPayload);
        SyncCryptoUtil.WriteBytes(writer, payload.EncryptedUserDevicesDataPayload);
        writer.Write(payload.UserDataLastModifiedAt.ToUniversalTime().ToUnixTimeMilliseconds());
        writer.Write(payload.GeneralUserDataLastModifiedAt.ToUniversalTime().ToUnixTimeMilliseconds());
        writer.Write(payload.UserPasswordsDataLastModifiedAt.ToUniversalTime().ToUnixTimeMilliseconds());
        writer.Write(payload.UserDevicesDataLastModifiedAt.ToUniversalTime().ToUnixTimeMilliseconds());
        SyncCryptoUtil.WriteBytes(writer, payload.IntegrityHash);

        var groupIds = payload.GroupIds
            .OrderBy(id => id)
            .ToArray();
        writer.Write(groupIds.Length);
        foreach (var groupId in groupIds)
            writer.Write(groupId.ToByteArray());

        var deviceIds = payload.DeviceIds
            .OrderBy(id => id)
            .ToArray();
        writer.Write(deviceIds.Length);
        foreach (var deviceId in deviceIds)
            writer.Write(deviceId.ToByteArray());
    }

    private static void ValidateUnsignedEnvelope(UserSnapshotEnvelope envelope)
    {
        if (envelope.UserId == Guid.Empty ||
            envelope.OriginDeviceId == Guid.Empty ||
            envelope.OriginInstanceId == Guid.Empty)
        {
            throw new InvalidDataException("User snapshot identity is incomplete.");
        }

        if (envelope.OriginRevision <= 0 ||
            envelope.UserKeyEpoch <= 0 ||
            envelope.MembershipEpoch <= 0)
        {
            throw new InvalidDataException("User snapshot revision or epoch is invalid.");
        }

        if (envelope.CreatedAtUtc == default)
            throw new InvalidDataException("User snapshot creation time is missing.");

        if (envelope.User is null)
            throw new InvalidDataException("User snapshot payload is missing.");
        if (envelope.Coverage is null)
            throw new InvalidDataException("User snapshot merged coverage is missing.");
        if (envelope.User.GroupIds is null || envelope.User.DeviceIds is null)
            throw new InvalidDataException("User snapshot relationship coverage is missing.");

        if (envelope.User.UId != envelope.UserId)
            throw new InvalidDataException("User snapshot payload user id does not match the envelope.");

        SyncVersionStampComparer.Validate(envelope.User.GeneralUserDataVersion);
        if (envelope.User.UsernameHash.Length != CryptographyConstants.Sha256HashSizeInBytes ||
            envelope.User.UsernameSalt.Length != CryptographyConstants.Sha256HashSizeInBytes)
            throw new InvalidDataException("User snapshot username projection metadata is invalid.");

        if (envelope.User.IntegrityHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
            throw new InvalidDataException("User snapshot payload integrity hash is invalid.");

        var expectedUserHash = SyncCryptoUtil.CalculateUserHash(
            envelope.User,
            envelope.CreatedAtUtc.ToUniversalTime().ToUnixTimeMilliseconds());
        if (!Hashing.Verify(envelope.User.IntegrityHash, expectedUserHash))
            throw new InvalidDataException("User snapshot payload integrity hash is invalid.");

        if (envelope.Coverage.Count > SyncConstants.MaxIncomingDeltaCountPerCall)
            throw new InvalidDataException("User snapshot coverage is too large.");

        if (envelope.User.GroupIds.Any(id => id == Guid.Empty) ||
            envelope.User.GroupIds.Count != envelope.User.GroupIds.Distinct().Count())
        {
            throw new InvalidDataException("User snapshot group coverage contains invalid or duplicate ids.");
        }

        if (envelope.User.DeviceIds.Any(id => id == Guid.Empty) ||
            envelope.User.DeviceIds.Count != envelope.User.DeviceIds.Distinct().Count())
        {
            throw new InvalidDataException("User snapshot device coverage contains invalid or duplicate ids.");
        }

        if (envelope.Coverage.Any(entry =>
                entry.OriginDeviceId == Guid.Empty ||
                entry.OriginInstanceId == Guid.Empty ||
                entry.UserKeyEpoch <= 0 ||
                entry.UserKeyEpoch > envelope.UserKeyEpoch ||
                entry.OriginRevision <= 0) ||
            envelope.Coverage.Count != envelope.Coverage
                .Select(entry => (entry.OriginDeviceId, entry.OriginInstanceId, entry.UserKeyEpoch))
                .Distinct()
                .Count())
        {
            throw new InvalidDataException("User snapshot merged coverage is invalid or contains duplicate origins.");
        }
    }

    private static void ValidateSignedEnvelope(UserSnapshotEnvelope envelope)
    {
        ValidateUnsignedEnvelope(envelope);

        if (envelope.SnapshotHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
            throw new InvalidDataException("User snapshot hash is missing.");

        if (envelope.OriginSignPublicKey.Length != SyncConstants.SyncDeltaEd25519PublicKeyBytes)
            throw new InvalidDataException("User snapshot origin signing key is invalid.");

        if (envelope.OriginSignature.Length != SyncConstants.SyncDeltaEd25519SignatureBytes)
            throw new InvalidDataException("User snapshot origin signature is invalid.");
    }
}
