using System.Text.Json;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Sync;

internal static class DeviceRemovalForkProofUtil
{
    internal static (UserSnapshotEnvelope First, UserSnapshotEnvelope Second) Validate(DeviceRemovalPayload removal)
    {
        var proof = removal.ForkExclusion ?? throw new InvalidDataException("The fork exclusion proof is absent.");
        // Control-envelope framing imposes its own tighter total bound after serialization.
        if (proof.AuthorizationId == Guid.Empty || proof.FirstEnvelope is not { Length: > 0 and <= SyncConstants.MaxUserSnapshotEnvelopeBytes } ||
            proof.SecondEnvelope is not { Length: > 0 and <= SyncConstants.MaxUserSnapshotEnvelopeBytes })
            throw new InvalidDataException("The fork exclusion evidence has an invalid size or identity.");
        var first = JsonSerializer.Deserialize(proof.FirstEnvelope, BackendJsonSerializerContext.Default.UserSnapshotEnvelope)
            ?? throw new InvalidDataException("The first fork envelope is missing.");
        var second = JsonSerializer.Deserialize(proof.SecondEnvelope, BackendJsonSerializerContext.Default.UserSnapshotEnvelope)
            ?? throw new InvalidDataException("The second fork envelope is missing.");
        UserSnapshotEnvelopeUtil.VerifyWithSigningKey(first, first.OriginSignPublicKey);
        UserSnapshotEnvelopeUtil.VerifyWithSigningKey(second, first.OriginSignPublicKey);
        if (first.UserId != removal.UserId || second.UserId != first.UserId ||
            first.OriginDeviceId != removal.RemovedDeviceId || second.OriginDeviceId != first.OriginDeviceId ||
            first.OriginInstanceId != second.OriginInstanceId || first.UserKeyEpoch != second.UserKeyEpoch ||
            first.MembershipEpoch != second.MembershipEpoch || first.OriginRevision != second.OriginRevision ||
            first.MembershipEpoch > removal.PreviousMembershipEpoch || first.UserKeyEpoch > removal.KeyEpoch ||
            Hashing.Verify(first.SnapshotHash, second.SnapshotHash) ||
            removal.Origins.Any(origin => origin.AuthorizationId != proof.AuthorizationId ||
                origin.OriginInstanceId != first.OriginInstanceId || origin.HighestAcceptedSnapshotRevision != 0 ||
                origin.HighestAcceptedControlSequence != 0 ||
                !Hashing.Verify(origin.SignPublicKeyHash, Hashing.SHA256Hash(first.OriginSignPublicKey))))
            throw new InvalidDataException("The signed evidence does not prove one exact excluded installation fork.");
        return (first, second);
    }

    internal static void VerifyAuthorization(DeviceRemovalPayload removal, UserMembershipAuthorization authorization)
    {
        var (first, _) = Validate(removal);
        if (authorization.AuthorizationId != removal.ForkExclusion!.AuthorizationId ||
            authorization.UserId != first.UserId || authorization.DeviceId != first.OriginDeviceId ||
            authorization.OriginInstanceId != first.OriginInstanceId || authorization.IsGenesis ||
            authorization.StartedMembershipEpoch > first.MembershipEpoch ||
            authorization.EndedMembershipEpoch <= first.MembershipEpoch ||
            authorization.MinimumKeyEpoch > first.UserKeyEpoch || authorization.MaximumKeyEpoch < first.UserKeyEpoch ||
            !Hashing.Verify(authorization.SignPublicKey, first.OriginSignPublicKey))
            throw new InvalidDataException("The fork proof is outside its exact historical authorization.");
    }
}
