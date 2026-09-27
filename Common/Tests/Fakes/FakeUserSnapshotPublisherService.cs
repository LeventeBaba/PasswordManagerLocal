using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Text.Json;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeUserSnapshotPublisherService : IUserSnapshotPublisherService
{
    private readonly IDeviceIdentityService _identity;
    private UserSyncSnapshot? _latest;

    public FakeUserSnapshotPublisherService(IDeviceIdentityService identity)
    {
        _identity = identity;
    }

    public Task<UserSyncSnapshot?> GetLatestAsync(Guid userId, long userKeyEpoch, CancellationToken ct = default) =>
        Task.FromResult(_latest is { UserId: var id, UserKeyEpoch: var epoch } && id == userId && epoch == userKeyEpoch ? _latest : null);

    public Task<UserSyncSnapshot> GetOrCreateAfterRecoveryAsync(
        User user,
        PasswordManagerLocal.Common.Backend.Security.EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        CancellationToken ct = default) =>
        GetOrCreateAsync(user, ct);

    public Task<UserSyncSnapshot> GetOrCreateAsync(User user, CancellationToken ct = default)
    {
        if (_latest is { UserId: var id, UserKeyEpoch: var epoch } && id == user.UId && epoch == user.KeyEpoch)
            return Task.FromResult(_latest);

        var createdAt = DateTimeOffset.UtcNow;
        var payload = new UserSyncPayload
        {
            UId = user.UId,
            UsernameHash = user.UsernameHash.ToArray(),
            UsernameSalt = user.UsernameSalt.ToArray(),
            GeneralUserDataVersion = user.GetGeneralUserDataVersion(),
            PasswordSalt = user.PasswordSalt.ToArray(),
            EncryptedPayload = user.EncryptedPayload.ToArray(),
            EncryptedGeneralUserDataPayload = user.EncryptedGeneralUserDataPayload.ToArray(),
            EncryptedUserPasswordsDataPayload = user.EncryptedUserPasswordsDataPayload.ToArray(),
            EncryptedUserDevicesDataPayload = user.EncryptedUserDevicesDataPayload.ToArray(),
            UserDataLastModifiedAt = user.UserDataLastModifiedAt,
            GeneralUserDataLastModifiedAt = user.GeneralUserDataLastModifiedAt,
            UserPasswordsDataLastModifiedAt = user.UserPasswordsDataLastModifiedAt,
            UserDevicesDataLastModifiedAt = user.UserDevicesDataLastModifiedAt,
            GroupIds = user.Groups.Select(group => group.Id).Distinct().OrderBy(id => id).ToList(),
            DeviceIds = user.UserDevices.Where(link => !link.IsDeleted).Select(link => link.DeviceId)
                .Append(_identity.LocalDeviceId).Distinct().OrderBy(id => id).ToList()
        };
        payload.IntegrityHash = SyncCryptoUtil.CalculateUserHash(payload, createdAt.ToUnixTimeMilliseconds());

        var envelope = new UserSnapshotEnvelope
        {
            UserId = user.UId,
            OriginDeviceId = _identity.LocalDeviceId,
            OriginInstanceId = _identity.OriginInstanceId,
            OriginRevision = 1,
            UserKeyEpoch = user.KeyEpoch,
            MembershipEpoch = user.MembershipEpoch,
            CreatedAtUtc = createdAt,
            User = payload
        };
        UserSnapshotEnvelopeUtil.FillOriginAuthentication(envelope, _identity);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, BackendJsonSerializerContext.Default.UserSnapshotEnvelope);
        _latest = new UserSyncSnapshot
        {
            UserId = user.UId,
            OriginDeviceId = envelope.OriginDeviceId,
            OriginInstanceId = envelope.OriginInstanceId,
            OriginRevision = envelope.OriginRevision,
            UserKeyEpoch = envelope.UserKeyEpoch,
            MembershipEpoch = envelope.MembershipEpoch,
            CreatedAtUtc = envelope.CreatedAtUtc,
            ReceivedAtUtc = envelope.CreatedAtUtc,
            SnapshotHash = envelope.SnapshotHash,
            OriginSignPublicKey = envelope.OriginSignPublicKey,
            OriginSignature = envelope.OriginSignature,
            EnvelopePayload = bytes,
            Status = UserSyncSnapshotStatus.LocalPublished
        };
        return Task.FromResult(_latest);
    }
}
