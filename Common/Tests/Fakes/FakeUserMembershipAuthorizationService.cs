using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeUserMembershipAuthorizationService : IUserMembershipAuthorizationService
{
    public Task<UserMembershipAuthorization> CreateGenesisAsync(User user, CancellationToken ct = default) =>
        Task.FromResult(Create(user.UId, Guid.NewGuid(), Guid.NewGuid(), user.KeyEpoch, user.MembershipEpoch, []));

    public Task<UserMembershipAuthorization> AuthorizeAdditionAsync(DeviceAdditionPayload payload, Guid operationId, byte[] operationHash, CancellationToken ct = default) =>
        Task.FromResult(Create(payload.UserId, payload.NewDeviceId, payload.NewOriginInstanceId, payload.KeyEpoch, payload.ResultingMembershipEpoch, payload.SignPublicKey));

    public Task EndAuthorizationAsync(UserMembershipAuthorization authorization, DeviceRemovalPayload payload, Guid operationId, byte[] operationHash, CancellationToken ct = default)
    {
        authorization.IsActive = false;
        authorization.EndedMembershipEpoch = payload.ResultingMembershipEpoch;
        return Task.CompletedTask;
    }

    public Task<UserMembershipAuthorization> VerifySnapshotAuthorAsync(UserSnapshotEnvelope envelope, CancellationToken ct = default)
    {
        UserSnapshotEnvelopeUtil.VerifyWithSigningKey(envelope, envelope.OriginSignPublicKey);
        return Task.FromResult(Create(envelope.UserId, envelope.OriginDeviceId, envelope.OriginInstanceId, envelope.UserKeyEpoch, envelope.MembershipEpoch, envelope.OriginSignPublicKey));
    }

    public Task<UserMembershipAuthorization> VerifyControlAuthorAsync(UserControlOperationEnvelope envelope, CancellationToken ct = default)
    {
        UserControlOperationEnvelopeUtil.VerifyWithSigningKey(envelope, envelope.OriginSignPublicKey);
        return Task.FromResult(Create(envelope.UserId, envelope.OriginDeviceId, envelope.OriginInstanceId, envelope.PreviousKeyEpoch, envelope.PreviousMembershipEpoch, envelope.OriginSignPublicKey));
    }

    private static UserMembershipAuthorization Create(Guid userId, Guid deviceId, Guid originId, long keyEpoch, long membershipEpoch, byte[] signKey) => new()
    {
        UserId = userId,
        DeviceId = deviceId,
        OriginInstanceId = originId,
        MinimumKeyEpoch = Math.Max(1, keyEpoch),
        StartedMembershipEpoch = Math.Max(1, membershipEpoch),
        SignPublicKey = signKey.ToArray(),
        SignPublicKeyHash = signKey.Length == 0 ? [] : PasswordManagerLocal.Common.Backend.Security.Hashing.SHA256Hash(signKey),
        IsActive = true
    };
}
