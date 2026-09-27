using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using System.Security.Cryptography;
using System.Text;

namespace PasswordManagerLocal.Common.Backend.Sync;

public static class SyncCryptoUtil
{
    public static byte[] BuildAssociatedData(NetworkDelta delta)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write("PasswordManagerLocal.Common.Backend.SyncDelta.AAD.v1");
        WriteString(bw, delta.Entity);
        bw.Write(delta.Ts);
        WriteString(bw, delta.DeviceId);
        WriteBytes(bw, delta.SignPub);
        WriteString(bw, delta.RecipientDeviceId);
        bw.Write(delta.EncryptionVersion);
        WriteBytes(bw, delta.PayloadHash);

        return ms.ToArray();
    }


    public static void ValidateEncryptedEnvelope(NetworkDelta delta, Guid localDeviceId)
    {
        if (string.IsNullOrWhiteSpace(delta.Entity) || delta.Entity.Length > 256)
            throw new InvalidDataException("Network delta entity is invalid.");

        if (delta.Ts <= 0)
            throw new InvalidDataException("Network delta timestamp is invalid.");

        var maxFutureTs = DateTimeOffset.UtcNow.AddSeconds(SyncConstants.MaxIncomingDeltaFutureSeconds).ToUnixTimeMilliseconds();
        if (delta.Ts > maxFutureTs)
            throw new InvalidDataException("Network delta timestamp is too far in the future.");

        if (string.IsNullOrWhiteSpace(delta.DeviceId))
            throw new InvalidDataException("Network delta source device id is missing.");

        if (delta.EncryptionVersion != SyncConstants.SyncDeltaEncryptionVersion)
            throw new InvalidDataException("Network delta encryption version is not supported.");

        if (string.IsNullOrWhiteSpace(delta.RecipientDeviceId))
            throw new InvalidDataException("Network delta recipient device id is missing.");

        if (!Guid.TryParseExact(delta.RecipientDeviceId, "N", out var recipientDeviceId) && !Guid.TryParse(delta.RecipientDeviceId, out recipientDeviceId))
            throw new InvalidDataException("Network delta recipient device id is invalid.");

        if (recipientDeviceId != localDeviceId)
            throw new UnauthorizedAccessException("Network delta was not encrypted for this device.");

        if (delta.Payload.Length == 0 || delta.Payload.Length > SyncConstants.MaxIncomingDeltaPayloadBytes)
            throw new InvalidDataException("Network delta payload size is invalid.");

        if (delta.EphemeralPublicKey.Length != SyncConstants.SyncDeltaX25519PublicKeyBytes)
            throw new InvalidDataException("Network delta ephemeral public key is invalid.");

        if (delta.Nonce.Length != SyncConstants.SyncDeltaNonceBytes)
            throw new InvalidDataException("Network delta nonce is invalid.");

        if (delta.Tag.Length != SyncConstants.SyncDeltaTagBytes)
            throw new InvalidDataException("Network delta authentication tag is invalid.");

        if (delta.PayloadHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
            throw new InvalidDataException("Network delta payload hash is invalid.");
    }


    public static void ValidatePlaintextHash(byte[] plaintext, byte[] expectedHash)
    {
        var actualHash = Hashing.SHA256Hash(plaintext);
        if (!Hashing.Verify(expectedHash, actualHash))
            throw new InvalidDataException("Network delta plaintext hash is invalid.");
    }


    public static void ValidatePayloadShape(SyncDeltaPayload payload)
    {
        var payloadCount = 0;
        if (payload.UserSnapshot is not null) payloadCount++;
        if (payload.UserControlOperation is not null) payloadCount++;
        if (payload.Group is not null) payloadCount++;
        if (payload.Device is not null) payloadCount++;
        if (payload.UserDevice is not null) payloadCount++;

        if (payload.ModelType == SyncModelType.User && payload.ChangeType == SyncChangeType.Deleted)
            throw new InvalidDataException("Generic user-deletion deltas are unsupported; signed AccountDeletion control operations are required.");

        if (payload.ChangeType == SyncChangeType.Deleted && payload.ModelType != SyncModelType.UserDevice)
        {
            if (payloadCount != 0)
                throw new InvalidDataException("Deleted delta cannot contain a full payload for this model type.");

            return;
        }

        if (payloadCount != 1)
            throw new InvalidDataException("Network delta must contain exactly one matching payload.");

        if (payload.ModelType == SyncModelType.User && payload.ChangeType != SyncChangeType.Deleted)
        {
            var hasSnapshot = payload.UserSnapshot is not null;
            var hasControlOperation = payload.UserControlOperation is not null;
            if (hasSnapshot == hasControlOperation)
                throw new InvalidDataException("User synchronization requires exactly one immutable snapshot or authoritative control operation.");
        }

        if (payload.ModelType == SyncModelType.Group && payload.Group is null)
            throw new InvalidDataException("Group sync payload is missing.");

        if (payload.ModelType == SyncModelType.Device && payload.Device is null)
            throw new InvalidDataException("Device sync payload is missing.");

        if (payload.ModelType == SyncModelType.UserDevice && payload.UserDevice is null)
            throw new InvalidDataException("User device sync payload is missing.");
    }


    public static void ValidatePayloadIntegrity(SyncDeltaPayload payload, long timestamp)
    {
        ValidatePayloadShape(payload);

        if (payload.ChangeType == SyncChangeType.Deleted && payload.ModelType != SyncModelType.UserDevice)
            return;

        if (payload.ModelType == SyncModelType.User)
        {
            if (payload.UserControlOperation is not null)
            {
                UserControlOperationEnvelopeUtil.ValidateStructureAndHash(payload.UserControlOperation);
                return;
            }

            if (payload.UserSnapshot is null)
                throw new InvalidDataException("User snapshot envelope is missing.");

            UserSnapshotEnvelopeUtil.ValidateStructureAndHash(payload.UserSnapshot);
            return;
        }

        if (payload.ModelType == SyncModelType.Group)
        {
            if (payload.Group is null || payload.Group.IntegrityHash.Length != CryptographyConstants.Sha256HashSizeInBytes)
                throw new InvalidDataException("Group sync hash is missing.");

            if (!Hashing.Verify(payload.Group.IntegrityHash, CalculateGroupHash(payload.Group, timestamp)))
                throw new InvalidDataException("Group sync hash is invalid.");

            return;
        }

        if (payload.ModelType == SyncModelType.Device)
        {
            if (payload.Device is null || payload.Device.IntegrityHash.Length != CryptographyConstants.Sha256HashSizeInBytes)
                throw new InvalidDataException("Device sync hash is missing.");

            if (!Hashing.Verify(payload.Device.IntegrityHash, CalculateDeviceHash(payload.Device, timestamp)))
                throw new InvalidDataException("Device sync hash is invalid.");

            return;
        }

        if (payload.ModelType == SyncModelType.UserDevice)
        {
            if (payload.UserDevice is null || payload.UserDevice.IntegrityHash.Length != CryptographyConstants.Sha256HashSizeInBytes)
                throw new InvalidDataException("User device sync hash is missing.");

            if (!Hashing.Verify(payload.UserDevice.IntegrityHash, SyncHashUtil.CalculateUserDeviceHash(payload.UserDevice, timestamp)))
                throw new InvalidDataException("User device sync hash is invalid.");
        }
    }


    public static byte[] CalculateUserHash(UserSyncPayload payload, long timestamp) =>
        Hashing.SHA256Hash(hash =>
        {
            hash.Write(payload.UId);
            hash.WriteBytes(payload.UsernameHash);
            hash.WriteBytes(payload.UsernameSalt);
            payload.GeneralUserDataVersion.WriteTo(hash);
            hash.WriteBytes(payload.PasswordSalt);
            hash.WriteBytes(payload.EncryptedPayload);
            hash.WriteBytes(payload.EncryptedGeneralUserDataPayload);
            hash.WriteBytes(payload.EncryptedUserPasswordsDataPayload);
            hash.WriteBytes(payload.EncryptedUserDevicesDataPayload);
            hash.Write(payload.UserDataLastModifiedAt);
            hash.Write(payload.GeneralUserDataLastModifiedAt);
            hash.Write(payload.UserPasswordsDataLastModifiedAt);
            hash.Write(payload.UserDevicesDataLastModifiedAt);
            hash.Write(timestamp);

            foreach (var groupId in payload.GroupIds.Where(id => id != Guid.Empty).Distinct().OrderBy(id => id))
                hash.Write(groupId);

            foreach (var deviceId in payload.DeviceIds.Where(id => id != Guid.Empty).Distinct().OrderBy(id => id))
                hash.Write(deviceId);
        });



    public static byte[] CalculateGroupHash(GroupSyncPayload payload, long timestamp) =>
        Hashing.SHA256Hash(hash =>
        {
            hash.Write(payload.Id);
            hash.WriteBytes(payload.EncryptedPayload);
            hash.Write(timestamp);

            foreach (var userId in payload.UserIds.Where(id => id != Guid.Empty).Distinct().OrderBy(id => id))
                hash.Write(userId);
        });



    public static byte[] CalculateDeviceHash(DeviceSyncPayload payload, long timestamp) =>
        Hashing.SHA256Hash(hash =>
        {
            hash.Write(payload.Id);
            hash.WriteBytes(payload.PublicKey);
            hash.WriteBytes(payload.SignPublicKey);
            hash.WriteString(payload.TlsCertFingerprint);
            hash.Write((byte)payload.DeviceType);
            hash.WriteBytes(payload.LastKnownHash);
            hash.Write(payload.LastSync);
            hash.Write(payload.LastSeen);
            hash.Write(payload.IsTrusted);
            hash.Write(payload.IsBlocked);
            hash.WriteString(payload.BlockedReason);
            hash.Write(payload.BlockedAt);
            hash.Write(payload.InvalidSyncAttemptCount);
            hash.Write(payload.LastInvalidSyncAttemptAt);
            hash.Write(timestamp);

            foreach (var userId in payload.UserIds.Where(id => id != Guid.Empty).Distinct().OrderBy(id => id))
                hash.Write(userId);
        });



    public static void WriteString(BinaryWriter writer, string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }


    public static void WriteBytes(BinaryWriter writer, byte[]? value)
    {
        var bytes = value ?? [];
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}
