using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Sync;

internal static class UserControlOperationMapping
{
    internal static UserControlOperation ToStoredOperation(
        UserControlOperationEnvelope envelope,
        byte[] serializedEnvelope,
        UserControlOperationStatus status,
        DateTimeOffset receivedAtUtc,
        DateTimeOffset? appliedAtUtc,
        Guid? lastReceivedFromDeviceId = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(serializedEnvelope);

        return new UserControlOperation
        {
            OperationId = envelope.OperationId,
            UserId = envelope.UserId,
            OperationType = envelope.OperationType,
            OriginDeviceId = envelope.OriginDeviceId,
            OriginInstanceId = envelope.OriginInstanceId,
            OriginSequence = envelope.OriginSequence,
            PreviousKeyEpoch = envelope.PreviousKeyEpoch,
            ResultingKeyEpoch = envelope.ResultingKeyEpoch,
            PreviousMembershipEpoch = envelope.PreviousMembershipEpoch,
            ResultingMembershipEpoch = envelope.ResultingMembershipEpoch,
            CreatedAtUtc = envelope.CreatedAtUtc,
            ReceivedAtUtc = receivedAtUtc,
            AppliedAtUtc = appliedAtUtc,
            LastReceivedFromDeviceId = lastReceivedFromDeviceId,
            PayloadHash = envelope.PayloadHash.ToArray(),
            OperationHash = envelope.OperationHash.ToArray(),
            OriginSignPublicKey = envelope.OriginSignPublicKey.ToArray(),
            OriginSignature = envelope.OriginSignature.ToArray(),
            EnvelopePayload = serializedEnvelope.ToArray(),
            Status = status
        };
    }
}
