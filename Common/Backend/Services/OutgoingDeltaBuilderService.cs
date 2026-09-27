using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Projections;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using System.Text.Json;

using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class OutgoingDeltaBuilderService : IOutgoingDeltaBuilderService
{
    private readonly IUserRepository _users;
    private readonly IGroupRepository _groups;
    private readonly IDeviceRepository _devices;
    private readonly IUserDeviceRepository _userDevices;
    private readonly ISyncRouteRepository _syncRoutes;
    private readonly IDeviceIdentityService _identity;
    private readonly IUserSnapshotPublisherService _snapshotPublisher;
    private readonly IUserMembershipAuthorizationService _membershipAuthorization;

    public OutgoingDeltaBuilderService(
        IUserRepository users,
        IGroupRepository groups,
        IDeviceRepository devices,
        IUserDeviceRepository userDevices,
        ISyncRouteRepository syncRoutes,
        IDeviceIdentityService identity,
        IUserSnapshotPublisherService snapshotPublisher,
        IUserMembershipAuthorizationService membershipAuthorization)
    {
        _users = users;
        _groups = groups;
        _devices = devices;
        _userDevices = userDevices;
        _syncRoutes = syncRoutes;
        _identity = identity;
        _snapshotPublisher = snapshotPublisher;
        _membershipAuthorization = membershipAuthorization;
    }




    public async Task<NetworkDelta> BuildAsync(SyncItem item, Device device, CancellationToken ct = default)
    {
        ValidateTargetDevice(device);
        var timestamp = item.ChangedAtTs > 0
            ? item.ChangedAtTs
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = await BuildPayloadAsync(item, device.Id, timestamp, ct);
        return BuildEncryptedDelta(payload, device, timestamp);
    }


    public async Task<NetworkDelta> BuildUserSnapshotRelayAsync(
        UserSyncSnapshot snapshot,
        Device device,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateTargetDevice(device);

        if (snapshot.Status is not (UserSyncSnapshotStatus.Pending or UserSyncSnapshotStatus.LocalPublished or UserSyncSnapshotStatus.MergedReceipt))
            throw new InvalidOperationException("Only pending, locally published, or merged-receipt user snapshots can be relayed.");

        var envelope = DeserializeSnapshot(snapshot);
        if (envelope.UserId != snapshot.UserId ||
            envelope.OriginDeviceId != snapshot.OriginDeviceId ||
            envelope.OriginInstanceId != snapshot.OriginInstanceId ||
            envelope.OriginRevision != snapshot.OriginRevision ||
            envelope.UserKeyEpoch != snapshot.UserKeyEpoch ||
            envelope.MembershipEpoch != snapshot.MembershipEpoch ||
            !Hashing.Verify(envelope.SnapshotHash, snapshot.SnapshotHash))
        {
            throw new InvalidDataException("Stored user snapshot metadata does not match its immutable envelope.");
        }

        await _membershipAuthorization.VerifySnapshotAuthorAsync(envelope, ct);

        var payload = new SyncDeltaPayload
        {
            ModelId = envelope.UserId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated,
            UserSnapshot = envelope
        };
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return BuildEncryptedDelta(payload, device, timestamp);
    }


    public async Task<NetworkDelta> BuildUserControlOperationRelayAsync(
        UserControlOperation operation,
        Device device,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(operation);
        ValidateTargetDevice(device);

        if (operation.Status is UserControlOperationStatus.Rejected or UserControlOperationStatus.Quarantined)
            throw new InvalidOperationException("Rejected or quarantined control operations are not relayable.");

        var envelope = UserControlOperationEnvelopeUtil.Deserialize(operation.EnvelopePayload);
        if (envelope.OperationId != operation.OperationId ||
            envelope.UserId != operation.UserId ||
            envelope.OriginDeviceId != operation.OriginDeviceId ||
            envelope.OriginInstanceId != operation.OriginInstanceId ||
            envelope.OriginSequence != operation.OriginSequence ||
            !Hashing.Verify(envelope.OperationHash, operation.OperationHash))
        {
            throw new InvalidDataException("Stored control-operation metadata does not match its immutable envelope.");
        }

        await _membershipAuthorization.VerifyControlAuthorAsync(envelope, ct);

        var payload = new SyncDeltaPayload
        {
            ModelId = envelope.UserId,
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated,
            UserControlOperation = envelope
        };
        return BuildEncryptedDelta(payload, device, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private Device BuildLocalOriginDevice() =>
        new()
        {
            Id = _identity.LocalDeviceId,
            SignPublicKey = _identity.SignPublicKey.ToArray(),
            PublicKey = _identity.AgreementPublicKey.ToArray(),
            TlsCertFingerprint = _identity.FingerprintHex,
            IsTrusted = true,
            IsBlocked = false
        };


    private NetworkDelta BuildEncryptedDelta(SyncDeltaPayload payload, Device device, long timestamp)
    {
        SyncCryptoUtil.ValidatePayloadIntegrity(payload, timestamp);
        UtcDateTimeUtil.NormalizeObjectGraph(payload);

        var plaintextPayload = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            BackendJsonSerializerContext.Default.SyncDeltaPayload);
        try
        {
            if (plaintextPayload.Length == 0 || plaintextPayload.Length > SyncConstants.MaxIncomingDeltaPayloadBytes)
                throw new InvalidDataException("Outgoing delta payload size is invalid.");

            var networkDelta = new NetworkDelta
            {
                Entity = BuildEntityName(payload),
                Ts = timestamp,
                DeviceId = _identity.DeviceIdHex,
                SignPub = _identity.SignPublicKey,
                RecipientDeviceId = device.Id.ToString("N"),
                EncryptionVersion = SyncConstants.SyncDeltaEncryptionVersion,
                PayloadHash = Hashing.SHA256Hash(plaintextPayload)
            };

            if (payload.UserSnapshot is not null)
            {
                networkDelta.SnapshotUserId = payload.UserSnapshot.UserId;
                networkDelta.SnapshotOriginDeviceId = payload.UserSnapshot.OriginDeviceId;
                networkDelta.SnapshotOriginInstanceId = payload.UserSnapshot.OriginInstanceId;
                networkDelta.SnapshotOriginRevision = payload.UserSnapshot.OriginRevision;
                networkDelta.SnapshotHash = payload.UserSnapshot.SnapshotHash.ToArray();
            }

            if (payload.UserControlOperation is not null)
            {
                networkDelta.ControlOperationId = payload.UserControlOperation.OperationId;
                networkDelta.ControlOperationUserId = payload.UserControlOperation.UserId;
                networkDelta.ControlOperationOriginDeviceId = payload.UserControlOperation.OriginDeviceId;
                networkDelta.ControlOperationOriginInstanceId = payload.UserControlOperation.OriginInstanceId;
                networkDelta.ControlOperationOriginSequence = payload.UserControlOperation.OriginSequence;
                networkDelta.ControlOperationHash = payload.UserControlOperation.OperationHash.ToArray();
            }

            var associatedData = SyncCryptoUtil.BuildAssociatedData(networkDelta);
            networkDelta.Payload = _identity.EncryptForDevice(
                plaintextPayload,
                device.PublicKey,
                associatedData,
                out var ephemeralPublicKey,
                out var nonce,
                out var tag);
            networkDelta.EphemeralPublicKey = ephemeralPublicKey;
            networkDelta.Nonce = nonce;
            networkDelta.Tag = tag;

            NetDeltaSigner.FillSignature(networkDelta, _identity);
            return networkDelta;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintextPayload);
        }
    }


    private void ValidateTargetDevice(Device device)
    {
        if (!_identity.IsSyncOn)
            throw new InvalidOperationException("Local synchronization is disabled.");
        if (device.Id == Guid.Empty)
            throw new InvalidOperationException("Target device is invalid.");
        if (device.PublicKey.Length == 0)
            throw new InvalidOperationException("Target device agreement key is missing.");
        if (!device.IsTrusted)
            throw new InvalidOperationException("Target device is not trusted.");
        if (device.IsBlocked)
            throw new InvalidOperationException("Target device is blocked.");
        if (IsLocalDevice(device))
            throw new InvalidOperationException("The local device cannot be a synchronization target.");
    }


    private async Task<SyncDeltaPayload> BuildPayloadAsync(SyncItem item, Guid targetDeviceId, long timestamp, CancellationToken ct)
    {
        if (item.ModelType == SyncModelType.User && item.ChangeType == SyncChangeType.Deleted)
            throw new InvalidOperationException("Generic user-deletion deltas are disabled; relay the signed AccountDeletion control operation.");

        var payload = new SyncDeltaPayload
        {
            ModelId = item.ModelId,
            ModelType = item.ModelType,
            ChangeType = item.ChangeType
        };

        if (item.ModelType == SyncModelType.UserDevice)
        {
            var userDevice = await _userDevices.GetByModelIdAsync(item.ModelId, ct);
            if (userDevice is null)
                throw new InvalidOperationException("User device sync source was not found.");

            payload.UserDevice = CreateUserDevicePayload(userDevice);
            return payload;
        }

        if (item.ChangeType == SyncChangeType.Deleted)
            return payload;

        if (item.ModelType == SyncModelType.User)
        {
            var user = await _users.GetByIdAsNoTrackingWithRelationsAsync(item.ModelId, ct);
            if (user is null)
                throw new InvalidOperationException("User sync source was not found.");

            var snapshot = await _snapshotPublisher.GetOrCreateAsync(user, ct);
            payload.UserSnapshot = DeserializeSnapshot(snapshot);
            return payload;
        }

        if (item.ModelType == SyncModelType.Group)
        {
            var group = await _groups.GetWithUserIdsAsNoTrackingAsync(item.ModelId, ct);
            if (group is null)
                throw new InvalidOperationException("Group sync source was not found.");

            payload.Group = CreateGroupPayload(group, timestamp);
            return payload;
        }

        if (item.ModelType == SyncModelType.Device)
        {
            var sourceDevice = await _devices.GetByIdAsNoTrackingWithUserDevicesAsync(item.ModelId, ct);
            if (sourceDevice is null)
                throw new InvalidOperationException("Device sync source was not found.");

            if (IsLocalDevice(sourceDevice))
                throw new InvalidOperationException("The local device cannot be synchronized as a stored device.");

            payload.Device = await CreateDevicePayloadAsync(sourceDevice, targetDeviceId, timestamp, ct);
            return payload;
        }

        throw new InvalidOperationException("Unknown sync model type.");
    }


    private UserSnapshotEnvelope DeserializeSnapshot(UserSyncSnapshot snapshot)
    {
        var envelope = JsonSerializer.Deserialize(
            snapshot.EnvelopePayload,
            BackendJsonSerializerContext.Default.UserSnapshotEnvelope)
            ?? throw new InvalidDataException("Stored local user snapshot is invalid.");
        UserSnapshotEnvelopeUtil.ValidateStructureAndHash(envelope);
        return envelope;
    }


    private GroupSyncPayload CreateGroupPayload(GroupWithUserIdsData group, long timestamp)
    {
        var payload = new GroupSyncPayload
        {
            Id = group.Id,
            EncryptedPayload = group.EncryptedPayload,
            UserIds = group.UserIds
        };

        payload.IntegrityHash = SyncCryptoUtil.CalculateGroupHash(payload, timestamp);
        return payload;
    }


    private async Task<DeviceSyncPayload> CreateDevicePayloadAsync(Device device, Guid targetDeviceId, long timestamp, CancellationToken ct)
    {
        foreach (var link in device.UserDevices)
            link.VerifyIntegrity();

        var candidateUserIds = device.UserDevices
            .Where(link => !link.IsDeleted && link.IsSyncOn)
            .Select(link => link.UserId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        var userIds = await _syncRoutes.ListEligibleUserIdsAsync(candidateUserIds, targetDeviceId, ct);

        var payload = new DeviceSyncPayload
        {
            Id = device.Id,
            PublicKey = device.PublicKey,
            SignPublicKey = device.SignPublicKey,
            TlsCertFingerprint = device.TlsCertFingerprint,
            DeviceType = device.DeviceType,
            LastKnownHash = device.LastKnownHash,
            LastSync = UtcDateTimeUtil.ToUtc(device.LastSync),
            LastSeen = UtcDateTimeUtil.ToUtc(device.LastSeen),
            IsTrusted = device.IsTrusted,
            IsBlocked = device.IsBlocked,
            BlockedReason = device.BlockedReason,
            BlockedAt = UtcDateTimeUtil.ToUtc(device.BlockedAt),
            InvalidSyncAttemptCount = device.InvalidSyncAttemptCount,
            LastInvalidSyncAttemptAt = UtcDateTimeUtil.ToUtc(device.LastInvalidSyncAttemptAt),
            UserIds = userIds.ToList()
        };

        payload.IntegrityHash = SyncCryptoUtil.CalculateDeviceHash(payload, timestamp);
        return payload;
    }


    private UserDeviceSyncPayload CreateUserDevicePayload(UserDevice userDevice)
    {
        userDevice.VerifyIntegrity();
        return new UserDeviceSyncPayload
        {
            UserId = userDevice.UserId,
            DeviceId = userDevice.DeviceId,
            IsSyncOn = userDevice.IsSyncOn,
            IsDeleted = userDevice.IsDeleted,
            DeletedAt = UtcDateTimeUtil.ToUtc(userDevice.DeletedAt),
            IntegrityHash = userDevice.IntegrityHash.ToArray()
        };
    }


    private bool IsLocalDevice(Device device) =>
        device.Id == _identity.LocalDeviceId ||
        device.SignPublicKey.SequenceEqual(_identity.SignPublicKey) ||
        string.Equals(FingerprintUtil.NormalizeOrEmpty(device.TlsCertFingerprint), FingerprintUtil.NormalizeOrEmpty(_identity.FingerprintHex), StringComparison.OrdinalIgnoreCase);




    private string BuildEntityName(SyncDeltaPayload payload) =>
        $"{payload.ModelType}:{payload.ChangeType}:{payload.ModelId:N}";
}
