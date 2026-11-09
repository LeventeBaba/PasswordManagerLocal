using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Sync;

/// <summary>Validates the complete removal projection before any membership row is changed.</summary>
internal static class DeviceRemovalEvidenceUtil
{
    internal static void ValidateActiveAuthorizations(
        DeviceRemovalPayload payload, IReadOnlyList<UserMembershipAuthorization> active)
    {
        UserControlOperationEnvelopeUtil.ValidateDeviceRemovalPayload(payload);
        if (active.Count == 0 || active.Any(row => !row.IsActive) ||
            !payload.Origins.Select(origin => origin.AuthorizationId).ToHashSet()
                .SetEquals(active.Select(row => row.AuthorizationId)))
            throw new InvalidDataException("The removal must cover exactly the active installation authorizations.");
        foreach (var authorization in active)
            ValidateAuthorization(payload, authorization);
    }

    internal static void ValidateAuthorization(DeviceRemovalPayload payload, UserMembershipAuthorization authorization)
    {
        var origins = payload.Origins.Where(origin => origin.AuthorizationId == authorization.AuthorizationId)
            .OrderBy(origin => origin.UserKeyEpoch).ToArray();
        var maximum = authorization.MaximumKeyEpoch ?? payload.KeyEpoch;
        if (authorization.UserId != payload.UserId || authorization.DeviceId != payload.RemovedDeviceId ||
            authorization.MinimumKeyEpoch <= 0 || maximum != payload.KeyEpoch ||
            maximum < authorization.MinimumKeyEpoch ||
            authorization.StartedMembershipEpoch > payload.PreviousMembershipEpoch ||
            origins.LongLength != maximum - authorization.MinimumKeyEpoch + 1)
            throw new InvalidDataException("The removal does not cover the installation's complete key-epoch range.");
        for (var index = 0; index < origins.Length; index++)
        {
            var origin = origins[index];
            if (origin.UserKeyEpoch != authorization.MinimumKeyEpoch + index ||
                origin.OriginInstanceId != authorization.OriginInstanceId ||
                !Hashing.Verify(origin.SignPublicKeyHash, authorization.SignPublicKeyHash) ||
                origin.AdditionOperationId != authorization.AdditionOperationId ||
                (origin.AdditionOperationHash is null) != (authorization.AdditionOperationHash is null) ||
                (origin.AdditionOperationHash is not null &&
                 !Hashing.Verify(origin.AdditionOperationHash, authorization.AdditionOperationHash!)))
                throw new InvalidDataException("The removal cutoff does not match the complete immutable authorization.");
        }
    }
}
