using System.Security.Cryptography;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Sync;

public static class DeletedUserBarrierUtil
{
    public static DeletedUserBarrier Create(
        UserControlOperationEnvelope envelope,
        AccountDeletionPayload payload,
        DateTimeOffset appliedAtUtc)
    {
        ValidateEnvelopePayloadMatch(envelope, payload);
        return new DeletedUserBarrier
        {
            UserId = envelope.UserId,
            DeletionOperationId = envelope.OperationId,
            DeletionGeneration = payload.DeletionGeneration,
            OriginDeviceId = envelope.OriginDeviceId,
            OriginInstanceId = envelope.OriginInstanceId,
            OriginSequence = envelope.OriginSequence,
            KeyEpoch = envelope.PreviousKeyEpoch,
            MembershipEpoch = envelope.PreviousMembershipEpoch,
            DeletedAtUtc = envelope.CreatedAtUtc,
            AppliedAtUtc = appliedAtUtc,
            LastUpdatedAtUtc = appliedAtUtc,
            OperationHash = envelope.OperationHash.ToArray(),
            OriginSignPublicKey = envelope.OriginSignPublicKey.ToArray(),
            OriginSignature = envelope.OriginSignature.ToArray()
        };
    }

    public static void ValidateEnvelopePayloadMatch(
        UserControlOperationEnvelope envelope,
        AccountDeletionPayload payload)
    {
        UserControlOperationEnvelopeUtil.ValidateStructureAndHash(envelope);
        UserControlOperationEnvelopeUtil.ValidateAccountDeletionPayload(payload);
        if (envelope.OperationType != UserControlOperationType.AccountDeletion ||
            payload.UserId != envelope.UserId ||
            payload.KeyEpoch != envelope.PreviousKeyEpoch ||
            payload.KeyEpoch != envelope.ResultingKeyEpoch ||
            payload.MembershipEpoch != envelope.PreviousMembershipEpoch ||
            payload.MembershipEpoch != envelope.ResultingMembershipEpoch)
        {
            throw new InvalidDataException("The account-deletion payload does not match its signed transition header.");
        }

        var originMember = payload.KnownMembers.SingleOrDefault(member =>
            member.DeviceId == envelope.OriginDeviceId &&
            member.OriginInstanceId == envelope.OriginInstanceId &&
            member.StartedMembershipEpoch <= envelope.PreviousMembershipEpoch &&
            (member.EndedMembershipEpoch is null || envelope.PreviousMembershipEpoch < member.EndedMembershipEpoch));
        if (originMember is null || !Hashing.Verify(originMember.SignPublicKeyHash, Hashing.SHA256Hash(envelope.OriginSignPublicKey)))
            throw new InvalidDataException("The account-deletion payload does not authenticate its origin membership identity.");
    }

    public static bool Matches(DeletedUserBarrier barrier, UserControlOperationEnvelope envelope) =>
        barrier.UserId == envelope.UserId &&
        barrier.DeletionOperationId == envelope.OperationId &&
        barrier.OriginDeviceId == envelope.OriginDeviceId &&
        barrier.OriginInstanceId == envelope.OriginInstanceId &&
        barrier.OriginSequence == envelope.OriginSequence &&
        barrier.OperationHash.Length == SyncConstants.SyncDeltaPayloadHashBytes &&
        CryptographicOperations.FixedTimeEquals(barrier.OperationHash, envelope.OperationHash);

    /// <summary>
    /// Returns a stable ordering for valid deletion operations. All valid operations imply deletion;
    /// the lexicographically smallest immutable operation hash, then operation id, is retained as the
    /// canonical barrier identity on every device.
    /// </summary>
    public static int CompareCanonical(
        DeletedUserBarrier current,
        UserControlOperationEnvelope candidate)
    {
        var hashComparison = current.OperationHash.AsSpan().SequenceCompareTo(candidate.OperationHash);
        if (hashComparison != 0)
            return hashComparison;
        return current.DeletionOperationId.CompareTo(candidate.OperationId);
    }

    public static void ReplaceCanonical(
        DeletedUserBarrier barrier,
        UserControlOperationEnvelope envelope,
        AccountDeletionPayload payload,
        DateTimeOffset appliedAtUtc)
    {
        ValidateEnvelopePayloadMatch(envelope, payload);
        barrier.DeletionOperationId = envelope.OperationId;
        barrier.DeletionGeneration = payload.DeletionGeneration;
        barrier.OriginDeviceId = envelope.OriginDeviceId;
        barrier.OriginInstanceId = envelope.OriginInstanceId;
        barrier.OriginSequence = envelope.OriginSequence;
        barrier.KeyEpoch = envelope.PreviousKeyEpoch;
        barrier.MembershipEpoch = envelope.PreviousMembershipEpoch;
        barrier.DeletedAtUtc = envelope.CreatedAtUtc;
        barrier.AppliedAtUtc = appliedAtUtc;
        barrier.OperationHash = envelope.OperationHash.ToArray();
        barrier.OriginSignPublicKey = envelope.OriginSignPublicKey.ToArray();
        barrier.OriginSignature = envelope.OriginSignature.ToArray();
        barrier.LastUpdatedAtUtc = appliedAtUtc;
    }
}
