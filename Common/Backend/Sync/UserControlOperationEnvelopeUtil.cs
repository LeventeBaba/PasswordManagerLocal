using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Text.Json;

namespace PasswordManagerLocal.Common.Backend.Sync;

public static class UserControlOperationEnvelopeUtil
{
    private const string ContentDomain = "PasswordManagerLocal.Common.Backend.UserControlOperation.Content.v2";
    private const string SignatureDomain = "PasswordManagerLocal.Common.Backend.UserControlOperation.Signature.v2";

    public static void FillOriginAuthentication(UserControlOperationEnvelope envelope, IDeviceIdentityService identity)
    {
        ValidateUnsigned(envelope);
        if (envelope.OriginDeviceId != identity.LocalDeviceId ||
            envelope.OriginInstanceId != identity.OriginInstanceId)
        {
            throw new InvalidDataException("A device can only sign control operations for its own persisted origin identity.");
        }

        envelope.OriginSignPublicKey = identity.SignPublicKey.ToArray();
        envelope.PayloadHash = Hashing.SHA256Hash(envelope.OperationPayload);
        envelope.OperationHash = CalculateOperationHash(envelope);
        envelope.OriginSignature = identity.Sign(BuildSignatureBytes(envelope));
    }

    public static void ValidateStructureAndHash(UserControlOperationEnvelope envelope)
    {
        ValidateSigned(envelope);
        var payloadHash = Hashing.SHA256Hash(envelope.OperationPayload);
        if (!Hashing.Verify(envelope.PayloadHash, payloadHash))
            throw new InvalidDataException("The control-operation payload hash is invalid.");

        var operationHash = CalculateOperationHash(envelope);
        if (!Hashing.Verify(envelope.OperationHash, operationHash))
            throw new InvalidDataException("The control-operation hash is invalid.");
    }

    public static void VerifyWithSigningKey(UserControlOperationEnvelope envelope, ReadOnlySpan<byte> historicalSigningPublicKey)
    {
        ValidateStructureAndHash(envelope);
        if (!historicalSigningPublicKey.SequenceEqual(envelope.OriginSignPublicKey))
            throw new InvalidDataException("The control-operation signing key does not match immutable membership history.");
        var publicKey = PublicKey.Import(
            SignatureAlgorithm.Ed25519,
            historicalSigningPublicKey,
            KeyBlobFormat.RawPublicKey);
        if (!SignatureAlgorithm.Ed25519.Verify(publicKey, BuildSignatureBytes(envelope), envelope.OriginSignature))
            throw new InvalidDataException("The control-operation signature is invalid.");
    }

    public static byte[] Serialize(UserControlOperationEnvelope envelope)
    {
        ValidateStructureAndHash(envelope);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            BackendJsonSerializerContext.Default.UserControlOperationEnvelope);
        if (serialized.Length == 0 || serialized.Length > SyncConstants.MaxUserControlOperationEnvelopeBytes)
            throw new InvalidDataException("The control-operation envelope size is invalid.");
        return serialized;
    }

    public static UserControlOperationEnvelope Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > SyncConstants.MaxUserControlOperationEnvelopeBytes)
            throw new InvalidDataException("The stored control-operation envelope size is invalid.");

        var envelope = JsonSerializer.Deserialize(
            bytes,
            BackendJsonSerializerContext.Default.UserControlOperationEnvelope)
            ?? throw new InvalidDataException("The stored control-operation envelope is invalid.");
        ValidateStructureAndHash(envelope);
        return envelope;
    }

    public static byte[] SerializeKeyEpochReplacementPayload(KeyEpochReplacementPayload payload)
    {
        ValidateKeyEpochReplacementPayload(payload);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            BackendJsonSerializerContext.Default.KeyEpochReplacementPayload);
        if (bytes.Length == 0 || bytes.Length > SyncConstants.MaxUserControlOperationPayloadBytes)
            throw new InvalidDataException("The key-epoch replacement payload size is invalid.");
        return bytes;
    }

    public static KeyEpochReplacementPayload DeserializeKeyEpochReplacementPayload(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > SyncConstants.MaxUserControlOperationPayloadBytes)
            throw new InvalidDataException("The key-epoch replacement payload size is invalid.");

        var payload = JsonSerializer.Deserialize(
            bytes,
            BackendJsonSerializerContext.Default.KeyEpochReplacementPayload)
            ?? throw new InvalidDataException("The key-epoch replacement payload is invalid.");
        ValidateKeyEpochReplacementPayload(payload);
        return payload;
    }

    public static KeyEpochReplacementPayload CreateKeyEpochReplacementPayload(User user, long previousKeyEpoch)
    {
        if (user.KeyEpoch != checked(previousKeyEpoch + 1))
            throw new InvalidOperationException("The canonical user is not at the expected resulting key epoch.");
        user.GenerateIntegrityHash();
        return new KeyEpochReplacementPayload
        {
            UserId = user.UId,
            PreviousKeyEpoch = previousKeyEpoch,
            ResultingKeyEpoch = user.KeyEpoch,
            MembershipEpoch = user.MembershipEpoch,
            UsernameHash = user.UsernameHash.ToArray(),
            UsernameSalt = user.UsernameSalt.ToArray(),
            GeneralUserDataVersion = user.GetGeneralUserDataVersion(),
            PasswordSalt = user.PasswordSalt.ToArray(),
            EncryptedPayload = user.EncryptedPayload.ToArray(),
            EncryptedGeneralUserDataPayload = user.EncryptedGeneralUserDataPayload.ToArray(),
            EncryptedUserPasswordsDataPayload = user.EncryptedUserPasswordsDataPayload.ToArray(),
            EncryptedUserDevicesDataPayload = user.EncryptedUserDevicesDataPayload.ToArray(),
            LastModifiedAt = user.LastModifiedAt,
            UserDataLastModifiedAt = user.UserDataLastModifiedAt,
            GeneralUserDataLastModifiedAt = user.GeneralUserDataLastModifiedAt,
            UserPasswordsDataLastModifiedAt = user.UserPasswordsDataLastModifiedAt,
            UserDevicesDataLastModifiedAt = user.UserDevicesDataLastModifiedAt,
            UserIntegrityHash = user.IntegrityHash.ToArray()
        };
    }

    public static bool MatchesKeyEpochReplacementPayload(KeyEpochReplacementPayload payload, User user)
    {
        ValidateKeyEpochReplacementPayload(payload);
        return payload.UserId == user.UId &&
               payload.ResultingKeyEpoch == user.KeyEpoch &&
               payload.MembershipEpoch == user.MembershipEpoch &&
               payload.UsernameHash.SequenceEqual(user.UsernameHash) &&
               payload.UsernameSalt.SequenceEqual(user.UsernameSalt) &&
               SyncVersionStampComparer.Instance.Equals(payload.GeneralUserDataVersion, user.GetGeneralUserDataVersion()) &&
               payload.PasswordSalt.SequenceEqual(user.PasswordSalt) &&
               payload.EncryptedPayload.SequenceEqual(user.EncryptedPayload) &&
               payload.EncryptedGeneralUserDataPayload.SequenceEqual(user.EncryptedGeneralUserDataPayload) &&
               payload.EncryptedUserPasswordsDataPayload.SequenceEqual(user.EncryptedUserPasswordsDataPayload) &&
               payload.EncryptedUserDevicesDataPayload.SequenceEqual(user.EncryptedUserDevicesDataPayload) &&
               payload.LastModifiedAt == user.LastModifiedAt &&
               payload.UserDataLastModifiedAt == user.UserDataLastModifiedAt &&
               payload.GeneralUserDataLastModifiedAt == user.GeneralUserDataLastModifiedAt &&
               payload.UserPasswordsDataLastModifiedAt == user.UserPasswordsDataLastModifiedAt &&
               payload.UserDevicesDataLastModifiedAt == user.UserDevicesDataLastModifiedAt &&
               payload.UserIntegrityHash.SequenceEqual(user.IntegrityHash);
    }

    public static void ApplyKeyEpochReplacementPayload(KeyEpochReplacementPayload payload, User user)
    {
        ValidateKeyEpochReplacementPayload(payload);
        if (payload.UserId != user.UId)
            throw new InvalidDataException("The key-epoch replacement targets a different user.");

        user.UsernameHash = payload.UsernameHash.ToArray();
        user.UsernameSalt = payload.UsernameSalt.ToArray();
        user.SetGeneralUserDataVersion(payload.GeneralUserDataVersion);
        user.PasswordSalt = payload.PasswordSalt.ToArray();
        user.EncryptedPayload = payload.EncryptedPayload.ToArray();
        user.EncryptedGeneralUserDataPayload = payload.EncryptedGeneralUserDataPayload.ToArray();
        user.EncryptedUserPasswordsDataPayload = payload.EncryptedUserPasswordsDataPayload.ToArray();
        user.EncryptedUserDevicesDataPayload = payload.EncryptedUserDevicesDataPayload.ToArray();
        user.SavedKey = null;
        user.KeyEpoch = payload.ResultingKeyEpoch;
        user.MembershipEpoch = payload.MembershipEpoch;
        user.LastModifiedAt = payload.LastModifiedAt;
        user.UserDataLastModifiedAt = payload.UserDataLastModifiedAt;
        user.GeneralUserDataLastModifiedAt = payload.GeneralUserDataLastModifiedAt;
        user.UserPasswordsDataLastModifiedAt = payload.UserPasswordsDataLastModifiedAt;
        user.UserDevicesDataLastModifiedAt = payload.UserDevicesDataLastModifiedAt;
        user.IntegrityHash = payload.UserIntegrityHash.ToArray();

        var calculated = user.CalculateIntegrityHash();
        if (!Hashing.Verify(user.IntegrityHash, calculated))
            throw new InvalidDataException("The replacement canonical user integrity hash is invalid.");
    }


    public static byte[] SerializeDeviceAdditionPayload(DeviceAdditionPayload payload)
    {
        ValidateDeviceAdditionPayload(payload);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, BackendJsonSerializerContext.Default.DeviceAdditionPayload);
        if (bytes.Length == 0 || bytes.Length > SyncConstants.MaxUserControlOperationPayloadBytes)
            throw new InvalidDataException("The device-addition payload size is invalid.");
        return bytes;
    }

    public static DeviceAdditionPayload DeserializeDeviceAdditionPayload(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > SyncConstants.MaxUserControlOperationPayloadBytes)
            throw new InvalidDataException("The device-addition payload size is invalid.");
        var payload = JsonSerializer.Deserialize(bytes, BackendJsonSerializerContext.Default.DeviceAdditionPayload)
            ?? throw new InvalidDataException("The device-addition payload is invalid.");
        ValidateDeviceAdditionPayload(payload);
        return payload;
    }

    public static byte[] SerializeDeviceRemovalPayload(DeviceRemovalPayload payload)
    {
        ValidateDeviceRemovalPayload(payload);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, BackendJsonSerializerContext.Default.DeviceRemovalPayload);
        if (bytes.Length == 0 || bytes.Length > SyncConstants.MaxUserControlOperationPayloadBytes)
            throw new InvalidDataException("The device-removal payload size is invalid.");
        return bytes;
    }

    public static DeviceRemovalPayload DeserializeDeviceRemovalPayload(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > SyncConstants.MaxUserControlOperationPayloadBytes)
            throw new InvalidDataException("The device-removal payload size is invalid.");
        var payload = JsonSerializer.Deserialize(bytes, BackendJsonSerializerContext.Default.DeviceRemovalPayload)
            ?? throw new InvalidDataException("The device-removal payload is invalid.");
        ValidateDeviceRemovalPayload(payload);
        return payload;
    }

    public static byte[] SerializeAccountDeletionPayload(AccountDeletionPayload payload)
    {
        ValidateAccountDeletionPayload(payload);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, BackendJsonSerializerContext.Default.AccountDeletionPayload);
        if (bytes.Length == 0 || bytes.Length > SyncConstants.MaxUserControlOperationPayloadBytes)
            throw new InvalidDataException("The account-deletion payload size is invalid.");
        return bytes;
    }

    public static AccountDeletionPayload DeserializeAccountDeletionPayload(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > SyncConstants.MaxUserControlOperationPayloadBytes)
            throw new InvalidDataException("The account-deletion payload size is invalid.");
        var payload = JsonSerializer.Deserialize(bytes, BackendJsonSerializerContext.Default.AccountDeletionPayload)
            ?? throw new InvalidDataException("The account-deletion payload is invalid.");
        ValidateAccountDeletionPayload(payload);
        return payload;
    }

    public static AccountDeletionPayload CreateAccountDeletionPayload(
        User user,
        IReadOnlyList<UserMembershipAuthorization> authorizations)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(authorizations);
        if (user.UId == Guid.Empty || user.KeyEpoch <= 0 || user.MembershipEpoch <= 0)
            throw new InvalidOperationException("The canonical account identity or epochs are invalid.");

        var payload = new AccountDeletionPayload
        {
            UserId = user.UId,
            DeletionGeneration = Guid.NewGuid(),
            KeyEpoch = user.KeyEpoch,
            MembershipEpoch = user.MembershipEpoch,
            KnownMembers = authorizations
                .OrderBy(row => row.AuthorizationId)
                .Select(row => new AccountDeletionKnownMember
                {
                    AuthorizationId = row.AuthorizationId,
                    DeviceId = row.DeviceId,
                    OriginInstanceId = row.OriginInstanceId,
                    StartedMembershipEpoch = row.StartedMembershipEpoch,
                    EndedMembershipEpoch = row.EndedMembershipEpoch,
                    MinimumKeyEpoch = row.MinimumKeyEpoch,
                    MaximumKeyEpoch = row.MaximumKeyEpoch,
                    SignPublicKeyHash = row.SignPublicKeyHash.ToArray(),
                    AdditionOperationId = row.AdditionOperationId,
                    RemovalOperationId = row.RemovalOperationId
                })
                .ToList()
        };
        FinalizeAccountDeletionPayload(payload);
        return payload;
    }

    public static void FinalizeAccountDeletionPayload(AccountDeletionPayload payload)
    {
        payload.MembershipHistoryHash = CalculateAccountDeletionMembershipHistoryHash(payload.KnownMembers);
        payload.IntegrityHash = CalculateAccountDeletionPayloadIntegrityHash(payload);
        ValidateAccountDeletionPayload(payload);
    }

    public static void ValidateAccountDeletionPayload(AccountDeletionPayload payload)
    {
        if (payload.UserId == Guid.Empty || payload.DeletionGeneration == Guid.Empty ||
            payload.KeyEpoch <= 0 || payload.MembershipEpoch <= 0 ||
            payload.KnownMembers.Count == 0 ||
            payload.KnownMembers.Count > SyncConstants.MaxUserControlInventoryEntries ||
            payload.MembershipHistoryHash.Length != SyncConstants.SyncDeltaPayloadHashBytes ||
            payload.IntegrityHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
        {
            throw new InvalidDataException("The account-deletion identity, epochs, or membership evidence are invalid.");
        }

        if (payload.KnownMembers.Any(member =>
                member.AuthorizationId == Guid.Empty || member.DeviceId == Guid.Empty || member.OriginInstanceId == Guid.Empty ||
                member.StartedMembershipEpoch <= 0 ||
                (member.EndedMembershipEpoch is long ended && ended <= member.StartedMembershipEpoch) ||
                member.MinimumKeyEpoch <= 0 ||
                (member.MaximumKeyEpoch is long maximum && maximum < member.MinimumKeyEpoch) ||
                member.SignPublicKeyHash.Length != SyncConstants.SyncDeltaPayloadHashBytes) ||
            payload.KnownMembers.Select(member => member.AuthorizationId).Distinct().Count() != payload.KnownMembers.Count ||
            payload.KnownMembers.Select(member => (member.DeviceId, member.OriginInstanceId)).Distinct().Count() != payload.KnownMembers.Count)
        {
            throw new InvalidDataException("The account-deletion membership evidence is invalid or duplicated.");
        }

        if (!Hashing.Verify(payload.MembershipHistoryHash, CalculateAccountDeletionMembershipHistoryHash(payload.KnownMembers)) ||
            !Hashing.Verify(payload.IntegrityHash, CalculateAccountDeletionPayloadIntegrityHash(payload)))
        {
            throw new InvalidDataException("The account-deletion payload integrity hash is invalid.");
        }
    }

    public static DeviceAdditionPayload CreateDeviceAdditionPayload(Guid userId, long keyEpoch, long previousMembershipEpoch, Guid deviceId, Guid originInstanceId, byte[] signPublicKey, byte[] agreementPublicKey, string tlsFingerprint, DeviceType deviceType)
    {
        var payload = new DeviceAdditionPayload
        {
            UserId = userId,
            NewDeviceId = deviceId,
            NewOriginInstanceId = originInstanceId,
            PreviousMembershipEpoch = previousMembershipEpoch,
            ResultingMembershipEpoch = checked(previousMembershipEpoch + 1),
            KeyEpoch = keyEpoch,
            SignPublicKey = signPublicKey.ToArray(),
            AgreementPublicKey = agreementPublicKey.ToArray(),
            TlsCertFingerprint = SyncIdentityUtil.NormalizeFingerprint(tlsFingerprint),
            DeviceType = deviceType
        };
        payload.IntegrityHash = CalculateDeviceAdditionPayloadIntegrityHash(payload);
        return payload;
    }

    public static void ValidateDeviceAdditionPayload(DeviceAdditionPayload payload)
    {
        if (payload.UserId == Guid.Empty || payload.NewDeviceId == Guid.Empty || payload.NewOriginInstanceId == Guid.Empty ||
            payload.KeyEpoch <= 0 || payload.PreviousMembershipEpoch <= 0 ||
            payload.ResultingMembershipEpoch != checked(payload.PreviousMembershipEpoch + 1))
            throw new InvalidDataException("The device-addition identity or epochs are invalid.");
        if (payload.SignPublicKey.Length != SyncConstants.SyncDeltaEd25519PublicKeyBytes ||
            payload.AgreementPublicKey.Length != SyncConstants.SyncDeltaX25519PublicKeyBytes ||
            payload.IntegrityHash.Length != SyncConstants.SyncDeltaPayloadHashBytes ||
            !DeviceTypeDetector.IsValid(payload.DeviceType))
            throw new InvalidDataException("The device-addition cryptographic identity is invalid.");
        payload.TlsCertFingerprint = SyncIdentityUtil.NormalizeFingerprint(payload.TlsCertFingerprint);
        if (!Hashing.Verify(payload.IntegrityHash, CalculateDeviceAdditionPayloadIntegrityHash(payload)))
            throw new InvalidDataException("The device-addition payload integrity hash is invalid.");
    }

    public static void FinalizeDeviceRemovalPayload(DeviceRemovalPayload payload)
    {
        payload.IntegrityHash = CalculateDeviceRemovalPayloadIntegrityHash(payload);
        ValidateDeviceRemovalPayload(payload);
    }

    public static void ValidateDeviceRemovalPayload(DeviceRemovalPayload payload)
    {
        if (payload.UserId == Guid.Empty || payload.RemovedDeviceId == Guid.Empty || payload.KeyEpoch <= 0 ||
            payload.PreviousMembershipEpoch <= 0 || payload.ResultingMembershipEpoch != checked(payload.PreviousMembershipEpoch + 1) ||
            payload.Origins.Count == 0 || payload.IntegrityHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
            throw new InvalidDataException("The device-removal identity, epochs, or cutoffs are invalid.");
        if (payload.Origins.Any(origin => origin.AuthorizationId == Guid.Empty || origin.OriginInstanceId == Guid.Empty ||
                origin.UserKeyEpoch <= 0 || origin.HighestAcceptedSnapshotRevision < 0 || origin.HighestAcceptedControlSequence < 0 ||
                origin.SignPublicKeyHash.Length != SyncConstants.SyncDeltaPayloadHashBytes ||
                (origin.AdditionOperationId is null) != (origin.AdditionOperationHash is null) ||
                (origin.AdditionOperationHash is not null && origin.AdditionOperationHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)) ||
            payload.Origins.Select(origin => (origin.AuthorizationId, origin.UserKeyEpoch)).Distinct().Count() != payload.Origins.Count)
            throw new InvalidDataException("The device-removal origin cutoffs are invalid or duplicated.");
        if (!Hashing.Verify(payload.IntegrityHash, CalculateDeviceRemovalPayloadIntegrityHash(payload)))
            throw new InvalidDataException("The device-removal payload integrity hash is invalid.");
    }

    private static byte[] CalculateAccountDeletionMembershipHistoryHash(IEnumerable<AccountDeletionKnownMember> members)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("PasswordManagerLocal.Common.Backend.AccountDeletionMembershipHistory.v1");
        var ordered = members.OrderBy(member => member.AuthorizationId).ToArray();
        writer.Write(ordered.Length);
        foreach (var member in ordered)
        {
            writer.Write(member.AuthorizationId.ToByteArray());
            writer.Write(member.DeviceId.ToByteArray());
            writer.Write(member.OriginInstanceId.ToByteArray());
            writer.Write(member.StartedMembershipEpoch);
            writer.Write(member.EndedMembershipEpoch.HasValue);
            if (member.EndedMembershipEpoch.HasValue) writer.Write(member.EndedMembershipEpoch.Value);
            writer.Write(member.MinimumKeyEpoch);
            writer.Write(member.MaximumKeyEpoch.HasValue);
            if (member.MaximumKeyEpoch.HasValue) writer.Write(member.MaximumKeyEpoch.Value);
            SyncCryptoUtil.WriteBytes(writer, member.SignPublicKeyHash);
            writer.Write(member.AdditionOperationId.HasValue);
            if (member.AdditionOperationId.HasValue) writer.Write(member.AdditionOperationId.Value.ToByteArray());
            writer.Write(member.RemovalOperationId.HasValue);
            if (member.RemovalOperationId.HasValue) writer.Write(member.RemovalOperationId.Value.ToByteArray());
        }
        return Hashing.SHA256Hash(stream.ToArray());
    }

    private static byte[] CalculateAccountDeletionPayloadIntegrityHash(AccountDeletionPayload payload)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("PasswordManagerLocal.Common.Backend.AccountDeletionPayload.v1");
        writer.Write(payload.UserId.ToByteArray());
        writer.Write(payload.DeletionGeneration.ToByteArray());
        writer.Write(payload.KeyEpoch);
        writer.Write(payload.MembershipEpoch);
        SyncCryptoUtil.WriteBytes(writer, payload.MembershipHistoryHash);
        return Hashing.SHA256Hash(stream.ToArray());
    }

    private static byte[] CalculateDeviceAdditionPayloadIntegrityHash(DeviceAdditionPayload payload)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("PasswordManagerLocal.Common.Backend.DeviceAdditionPayload.v1");
        writer.Write(payload.UserId.ToByteArray()); writer.Write(payload.NewDeviceId.ToByteArray()); writer.Write(payload.NewOriginInstanceId.ToByteArray());
        writer.Write(payload.PreviousMembershipEpoch); writer.Write(payload.ResultingMembershipEpoch); writer.Write(payload.KeyEpoch);
        SyncCryptoUtil.WriteBytes(writer, payload.SignPublicKey); SyncCryptoUtil.WriteBytes(writer, payload.AgreementPublicKey);
        writer.Write(SyncIdentityUtil.NormalizeFingerprint(payload.TlsCertFingerprint)); writer.Write((byte)payload.DeviceType);
        return Hashing.SHA256Hash(stream.ToArray());
    }

    private static byte[] CalculateDeviceRemovalPayloadIntegrityHash(DeviceRemovalPayload payload)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("PasswordManagerLocal.Common.Backend.DeviceRemovalPayload.v1");
        writer.Write(payload.UserId.ToByteArray()); writer.Write(payload.RemovedDeviceId.ToByteArray());
        writer.Write(payload.PreviousMembershipEpoch); writer.Write(payload.ResultingMembershipEpoch); writer.Write(payload.KeyEpoch);
        var origins = payload.Origins.OrderBy(origin => origin.AuthorizationId).ThenBy(origin => origin.UserKeyEpoch).ToArray();
        writer.Write(origins.Length);
        foreach (var origin in origins)
        {
            writer.Write(origin.AuthorizationId.ToByteArray()); writer.Write(origin.OriginInstanceId.ToByteArray());
            writer.Write(origin.UserKeyEpoch); writer.Write(origin.HighestAcceptedSnapshotRevision); writer.Write(origin.HighestAcceptedControlSequence);
            SyncCryptoUtil.WriteBytes(writer, origin.SignPublicKeyHash);
            writer.Write(origin.AdditionOperationId.HasValue);
            if (origin.AdditionOperationId.HasValue) writer.Write(origin.AdditionOperationId.Value.ToByteArray());
            SyncCryptoUtil.WriteBytes(writer, origin.AdditionOperationHash ?? []);
        }
        return Hashing.SHA256Hash(stream.ToArray());
    }

    public static byte[] CalculateOperationHash(UserControlOperationEnvelope envelope)
    {
        ValidateUnsigned(envelope);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(ContentDomain);
        writer.Write(envelope.OperationId.ToByteArray());
        writer.Write(envelope.UserId.ToByteArray());
        writer.Write((byte)envelope.OperationType);
        writer.Write(envelope.OriginDeviceId.ToByteArray());
        writer.Write(envelope.OriginInstanceId.ToByteArray());
        writer.Write(envelope.OriginSequence);
        writer.Write(envelope.PreviousKeyEpoch);
        writer.Write(envelope.ResultingKeyEpoch);
        writer.Write(envelope.PreviousMembershipEpoch);
        writer.Write(envelope.ResultingMembershipEpoch);
        writer.Write(envelope.CreatedAtUtc.ToUniversalTime().ToUnixTimeMilliseconds());
        SyncCryptoUtil.WriteBytes(writer, envelope.PayloadHash);
        SyncCryptoUtil.WriteBytes(writer, envelope.OriginSignPublicKey);
        return Hashing.SHA256Hash(stream.ToArray());
    }

    private static byte[] BuildSignatureBytes(UserControlOperationEnvelope envelope)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(SignatureDomain);
        writer.Write(envelope.OperationId.ToByteArray());
        writer.Write(envelope.UserId.ToByteArray());
        writer.Write(envelope.OriginDeviceId.ToByteArray());
        writer.Write(envelope.OriginInstanceId.ToByteArray());
        writer.Write(envelope.OriginSequence);
        SyncCryptoUtil.WriteBytes(writer, envelope.OperationHash);
        return stream.ToArray();
    }

    private static void ValidateUnsigned(UserControlOperationEnvelope envelope)
    {
        if (envelope.OperationId == Guid.Empty || envelope.UserId == Guid.Empty ||
            envelope.OriginDeviceId == Guid.Empty || envelope.OriginInstanceId == Guid.Empty)
        {
            throw new InvalidDataException("The control-operation identity is incomplete.");
        }
        if (!Enum.IsDefined(envelope.OperationType) || envelope.OriginSequence <= 0)
            throw new InvalidDataException("The control-operation type or origin sequence is invalid.");
        if (envelope.CreatedAtUtc == default)
            throw new InvalidDataException("The control-operation creation time is missing.");
        if (envelope.OperationPayload.Length == 0 ||
            envelope.OperationPayload.Length > SyncConstants.MaxUserControlOperationPayloadBytes)
        {
            throw new InvalidDataException("The control-operation payload size is invalid.");
        }

        switch (envelope.OperationType)
        {
            case UserControlOperationType.KeyEpochReplacement:
                if (envelope.PreviousKeyEpoch <= 0 ||
                    envelope.ResultingKeyEpoch != checked(envelope.PreviousKeyEpoch + 1) ||
                    envelope.PreviousMembershipEpoch <= 0 ||
                    envelope.ResultingMembershipEpoch != envelope.PreviousMembershipEpoch)
                {
                    throw new InvalidDataException("The key-epoch transition is invalid.");
                }
                break;
            case UserControlOperationType.DeviceAddition:
            case UserControlOperationType.DeviceRemoval:
                if (envelope.PreviousKeyEpoch <= 0 ||
                    envelope.ResultingKeyEpoch != envelope.PreviousKeyEpoch ||
                    envelope.PreviousMembershipEpoch <= 0 ||
                    envelope.ResultingMembershipEpoch != checked(envelope.PreviousMembershipEpoch + 1))
                {
                    throw new InvalidDataException("The membership operation must advance exactly one membership epoch without changing the key epoch.");
                }
                break;
            case UserControlOperationType.MembershipChange:
                throw new InvalidDataException("The obsolete generic membership operation is not supported.");
            case UserControlOperationType.AccountDeletion:
                if (envelope.PreviousKeyEpoch <= 0 ||
                    envelope.ResultingKeyEpoch != envelope.PreviousKeyEpoch ||
                    envelope.PreviousMembershipEpoch <= 0 ||
                    envelope.ResultingMembershipEpoch != envelope.PreviousMembershipEpoch)
                {
                    throw new InvalidDataException("Account deletion must preserve the final key and membership epochs in its signed header.");
                }
                break;
            default:
                throw new InvalidDataException("The control-operation type is unsupported.");
        }
    }

    private static void ValidateSigned(UserControlOperationEnvelope envelope)
    {
        ValidateUnsigned(envelope);
        if (envelope.PayloadHash.Length != SyncConstants.SyncDeltaPayloadHashBytes ||
            envelope.OperationHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
        {
            throw new InvalidDataException("The control-operation hashes are invalid.");
        }
        if (envelope.OriginSignPublicKey.Length != SyncConstants.SyncDeltaEd25519PublicKeyBytes ||
            envelope.OriginSignature.Length != SyncConstants.SyncDeltaEd25519SignatureBytes)
        {
            throw new InvalidDataException("The control-operation origin authentication is invalid.");
        }
    }

    private static void ValidateKeyEpochReplacementPayload(KeyEpochReplacementPayload payload)
    {
        if (payload.UserId == Guid.Empty || payload.PreviousKeyEpoch <= 0 ||
            payload.ResultingKeyEpoch != checked(payload.PreviousKeyEpoch + 1) ||
            payload.MembershipEpoch <= 0)
        {
            throw new InvalidDataException("The key-epoch replacement identity or epochs are invalid.");
        }

        SyncVersionStampComparer.Validate(payload.GeneralUserDataVersion);
        if (payload.UsernameHash.Length != CryptographyConstants.Sha256HashSizeInBytes ||
            payload.UsernameSalt.Length != CryptographyConstants.Sha256HashSizeInBytes ||
            payload.PasswordSalt.Length == 0 || payload.EncryptedPayload.Length == 0 ||
            payload.EncryptedGeneralUserDataPayload.Length == 0 ||
            payload.EncryptedUserPasswordsDataPayload.Length == 0 ||
            payload.EncryptedUserDevicesDataPayload.Length == 0 ||
            payload.UserIntegrityHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
        {
            throw new InvalidDataException("The key-epoch replacement canonical payload is incomplete.");
        }

        var candidate = new User
        {
            UId = payload.UserId,
            UsernameHash = payload.UsernameHash,
            UsernameSalt = payload.UsernameSalt,
            GeneralDataVersionPhysicalTimeUnixMilliseconds = payload.GeneralUserDataVersion.PhysicalTimeUnixMilliseconds,
            GeneralDataVersionLogicalCounter = payload.GeneralUserDataVersion.LogicalCounter,
            GeneralDataVersionOriginDeviceId = payload.GeneralUserDataVersion.OriginDeviceId,
            GeneralDataVersionOriginInstanceId = payload.GeneralUserDataVersion.OriginInstanceId,
            PasswordSalt = payload.PasswordSalt,
            EncryptedPayload = payload.EncryptedPayload,
            EncryptedGeneralUserDataPayload = payload.EncryptedGeneralUserDataPayload,
            EncryptedUserPasswordsDataPayload = payload.EncryptedUserPasswordsDataPayload,
            EncryptedUserDevicesDataPayload = payload.EncryptedUserDevicesDataPayload,
            KeyEpoch = payload.ResultingKeyEpoch,
            MembershipEpoch = payload.MembershipEpoch,
            LastModifiedAt = payload.LastModifiedAt,
            UserDataLastModifiedAt = payload.UserDataLastModifiedAt,
            GeneralUserDataLastModifiedAt = payload.GeneralUserDataLastModifiedAt,
            UserPasswordsDataLastModifiedAt = payload.UserPasswordsDataLastModifiedAt,
            UserDevicesDataLastModifiedAt = payload.UserDevicesDataLastModifiedAt,
            IntegrityHash = payload.UserIntegrityHash
        };
        if (!Hashing.Verify(payload.UserIntegrityHash, candidate.CalculateIntegrityHash()))
            throw new InvalidDataException("The key-epoch replacement canonical integrity hash is invalid.");
    }
}
