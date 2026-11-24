using System.Text.Json;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Sync;

public static class RevocationDeliveryReceiptUtil
{
    public static RevocationDeliveryReceipt Create(UserControlOperationEnvelope operation,
        UserMembershipAuthorization recipient, RevocationReceiptKind kind, IDeviceIdentityService identity, byte[]? attestedReceiptHash = null)
    {
        if (identity.LocalDeviceId != recipient.DeviceId || identity.OriginInstanceId != recipient.OriginInstanceId ||
            !identity.SignPublicKey.SequenceEqual(recipient.SignPublicKey))
            throw new UnauthorizedAccessException("Only the acknowledging installation can sign its receipt.");
        var receipt = new RevocationDeliveryReceipt
        {
            UserId = operation.UserId, OperationId = operation.OperationId, OperationHash = operation.OperationHash.ToArray(),
            OperationType = operation.OperationType, KeyEpoch = operation.PreviousKeyEpoch,
            MembershipEpoch = operation.PreviousMembershipEpoch, RecipientDeviceId = recipient.DeviceId,
            RecipientOriginInstanceId = recipient.OriginInstanceId, RecipientAuthorizationId = recipient.AuthorizationId,
            Kind = kind, AttestedReceiptHash = attestedReceiptHash?.ToArray() ?? [], SignPublicKey = identity.SignPublicKey.ToArray()
        };
        receipt.Signature = identity.Sign(SigningBytes(receipt));
        Verify(receipt, operation, recipient);
        return receipt;
    }

    public static void Verify(RevocationDeliveryReceipt receipt, UserControlOperationEnvelope operation,
        UserMembershipAuthorization recipient)
    {
        Validate(receipt);
        UserControlOperationEnvelopeUtil.ValidateStructureAndHash(operation);
        if (receipt.UserId != operation.UserId || receipt.OperationId != operation.OperationId ||
            !Hashing.Verify(receipt.OperationHash, operation.OperationHash) || receipt.OperationType != operation.OperationType ||
            receipt.KeyEpoch != operation.PreviousKeyEpoch || receipt.MembershipEpoch != operation.PreviousMembershipEpoch ||
            recipient.UserId != receipt.UserId || recipient.AuthorizationId != receipt.RecipientAuthorizationId ||
            recipient.DeviceId != receipt.RecipientDeviceId || recipient.OriginInstanceId != receipt.RecipientOriginInstanceId ||
            recipient.StartedMembershipEpoch > receipt.MembershipEpoch ||
            (recipient.EndedMembershipEpoch is long end && receipt.MembershipEpoch >= end) ||
            recipient.MinimumKeyEpoch > receipt.KeyEpoch ||
            (recipient.MaximumKeyEpoch is long maximum && receipt.KeyEpoch > maximum) ||
            !Hashing.Verify(recipient.SignPublicKeyHash, Hashing.SHA256Hash(recipient.SignPublicKey)) ||
            !recipient.SignPublicKey.SequenceEqual(receipt.SignPublicKey))
            throw new InvalidDataException("The receipt is not bound to this exact historical operation and installation.");
        // Deletion's signed member summary is an additional binding, never a replacement for retained history.
        if (operation.OperationType == UserControlOperationType.AccountDeletion)
        {
            var payload = UserControlOperationEnvelopeUtil.DeserializeAccountDeletionPayload(operation.OperationPayload);
            var member = payload.KnownMembers.SingleOrDefault(m => m.AuthorizationId == recipient.AuthorizationId);
            if (member is null || member.DeviceId != recipient.DeviceId || member.OriginInstanceId != recipient.OriginInstanceId ||
                !Hashing.Verify(member.SignPublicKeyHash, recipient.SignPublicKeyHash) ||
                member.StartedMembershipEpoch != recipient.StartedMembershipEpoch ||
                member.MinimumKeyEpoch != recipient.MinimumKeyEpoch || member.AdditionOperationId != recipient.AdditionOperationId ||
                member.StartedMembershipEpoch > receipt.MembershipEpoch ||
                (member.EndedMembershipEpoch is long memberEnd && receipt.MembershipEpoch >= memberEnd) ||
                (member.MaximumKeyEpoch is long memberMaximum && receipt.KeyEpoch > memberMaximum))
                throw new InvalidDataException("The receipt installation is absent from the signed deletion history.");
        }
        var key = PublicKey.Import(SignatureAlgorithm.Ed25519, recipient.SignPublicKey, KeyBlobFormat.RawPublicKey);
        if (!SignatureAlgorithm.Ed25519.Verify(key, SigningBytes(receipt), receipt.Signature))
            throw new InvalidDataException("The revocation receipt signature is invalid.");
    }

    // No clock or mutable transport fields: retries by an installation have one deterministic signed content.
    public static byte[] SigningBytes(RevocationDeliveryReceipt r)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("PasswordManagerLocal.RevocationDeliveryReceipt.v2");
        writer.Write(r.Version);
        writer.Write(r.UserId.ToByteArray()); writer.Write(r.OperationId.ToByteArray());
        writer.Write((byte)r.OperationType); writer.Write(r.KeyEpoch); writer.Write(r.MembershipEpoch);
        writer.Write(r.RecipientDeviceId.ToByteArray()); writer.Write(r.RecipientOriginInstanceId.ToByteArray());
        writer.Write(r.RecipientAuthorizationId.ToByteArray()); writer.Write((byte)r.Kind);
        SyncCryptoUtil.WriteBytes(writer, r.OperationHash); SyncCryptoUtil.WriteBytes(writer, r.SignPublicKey);
        SyncCryptoUtil.WriteBytes(writer, r.AttestedReceiptHash);
        return stream.ToArray();
    }

    public static byte[] Hash(RevocationDeliveryReceipt receipt)
    {
        Validate(receipt);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("PasswordManagerLocal.RevocationDeliveryReceipt.Hash.v2");
        SyncCryptoUtil.WriteBytes(writer, SigningBytes(receipt)); SyncCryptoUtil.WriteBytes(writer, receipt.Signature);
        return Hashing.SHA256Hash(stream.ToArray());
    }

    public static byte[] Serialize(RevocationDeliveryReceipt receipt)
    {
        Validate(receipt);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, BackendJsonSerializerContext.Default.RevocationDeliveryReceipt);
        if (bytes.Length > SyncConstants.MaxRevocationReceiptBytes)
            throw new InvalidDataException("The revocation receipt is too large.");
        return bytes;
    }

    public static RevocationDeliveryReceipt Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > SyncConstants.MaxRevocationReceiptBytes)
            throw new InvalidDataException("The revocation receipt size is invalid.");
        try
        {
            var receipt = JsonSerializer.Deserialize(bytes, BackendJsonSerializerContext.Default.RevocationDeliveryReceipt)
                ?? throw new InvalidDataException("The revocation receipt is missing.");
            Validate(receipt);
            return receipt;
        }
        catch (JsonException ex) { throw new InvalidDataException("The revocation receipt encoding is invalid.", ex); }
    }

    private static void Validate(RevocationDeliveryReceipt r)
    {
        if (r.Version != 2 || r.UserId == Guid.Empty || r.OperationId == Guid.Empty ||
            r.RecipientDeviceId == Guid.Empty || r.RecipientOriginInstanceId == Guid.Empty || r.RecipientAuthorizationId == Guid.Empty ||
            r.OperationType is not (UserControlOperationType.DeviceRemoval or UserControlOperationType.AccountDeletion) ||
            !Enum.IsDefined(r.Kind) || r.AttestedReceiptHash is null ||
            (r.Kind == RevocationReceiptKind.AppliedReceiptStored ? r.AttestedReceiptHash.Length != 32 : r.AttestedReceiptHash.Length != 0) ||
            r.KeyEpoch <= 0 || r.MembershipEpoch <= 0 ||
            r.OperationHash is not { Length: SyncConstants.SyncDeltaPayloadHashBytes } ||
            r.SignPublicKey is not { Length: SyncConstants.SyncDeltaEd25519PublicKeyBytes } ||
            r.Signature is not { Length: SyncConstants.SyncDeltaEd25519SignatureBytes })
            throw new InvalidDataException("The revocation receipt identity, version or authentication is invalid.");
    }
}
